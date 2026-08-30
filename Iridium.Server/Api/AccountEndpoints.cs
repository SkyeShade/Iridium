using System.Text.RegularExpressions;
using Iridium.Protocol;
using Iridium.Server.Configuration;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Iridium.Server.Profiles;
using System.Net.Mail;
using Microsoft.AspNetCore.RateLimiting;

namespace Iridium.Server.Api;

public static partial class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var accounts = endpoints.MapGroup("/api/account");
        accounts.MapPost("/register", RegisterAsync);
        accounts.MapPost("/login", LoginAsync);
        accounts.MapGet("/current", CurrentAsync);
        accounts.MapPatch("/current", UpdateCurrentAsync);
        accounts.MapPost("/logout", LogoutAsync);
        accounts.MapGet("/security", SecurityStatusAsync);
        accounts.MapPost("/security/password", ChangePasswordAsync);
        accounts.MapPut("/security/recovery-email", UpdateRecoveryEmailAsync);
        accounts.MapPost("/recovery/request", RequestPasswordRecoveryAsync)
            .RequireRateLimiting("password-recovery");
        accounts.MapPost("/recovery/validate", ValidatePasswordRecoveryAsync);
        accounts.MapPost("/recovery/complete", CompletePasswordRecoveryAsync);

        var communities = endpoints.MapGroup("/api/communities");
        communities.MapGet("/", ListCommunitiesAsync);
        communities.MapPost("/", CreateCommunityAsync);
        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterAccountRequest request,
        IridiumDbContext db,
        IAccountPasswordService passwords,
        SessionService sessions,
        IOptions<NodeOptions> options)
    {
        if (!options.Value.AllowRegistrations)
            return Results.Problem("This node is not accepting registrations.", statusCode: StatusCodes.Status403Forbidden);

        var errors = ValidateAccount(request.Username, request.DisplayName, request.Password);
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        var username = request.Username.Trim().ToLowerInvariant();
        if (await db.Accounts.AnyAsync(value => value.Username == username))
            return Results.Conflict(new { message = "That username is already registered on this node." });

        var account = new NodeAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };
        account.PasswordHash = passwords.HashPassword(account, request.Password);
        db.Accounts.Add(account);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { message = "That username is already registered on this node." });
        }

        var (token, _) = await sessions.CreateAsync(account, db);
        return Results.Ok(new AuthenticationResultDto(token, ToDto(account)));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IridiumDbContext db,
        IAccountPasswordService passwords,
        SessionService sessions)
    {
        var username = request.Username.Trim().ToLowerInvariant();
        var account = await db.Accounts.SingleOrDefaultAsync(value => value.Username == username);
        if (account is null)
            return Results.Unauthorized();

        var verification = passwords.VerifyPassword(account, request.Password);
        if (verification == AccountPasswordVerificationResult.Failed) return Results.Unauthorized();
        if (verification == AccountPasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = passwords.HashPassword(account, request.Password);
            await db.SaveChangesAsync();
        }

        var (token, _) = await sessions.CreateAsync(account, db);
        return Results.Ok(new AuthenticationResultDto(token, ToDto(account)));
    }

    private static async Task<IResult> CurrentAsync(HttpContext context, IridiumDbContext db, SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        return session is null ? Results.Unauthorized() : Results.Ok(ToDto(session.Account));
    }

    private static async Task<IResult> UpdateCurrentAsync(
        UpdateProfileRequest request,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        ProfileRealtimePublisher realtime)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();

        var displayName = request.DisplayName.Trim();
        var pronouns = string.IsNullOrWhiteSpace(request.Pronouns) ? null : request.Pronouns.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var errors = new Dictionary<string, string[]>();
        if (displayName.Length is < 1 or > 64)
            errors[nameof(request.DisplayName)] = ["Display names must be between 1 and 64 characters."];
        if (pronouns?.Length > 64)
            errors[nameof(request.Pronouns)] = ["Pronouns cannot exceed 64 characters."];
        if (description?.Length > 400)
            errors[nameof(request.Description)] = ["Profile descriptions cannot exceed 400 characters."];
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        session.Account.DisplayName = displayName;
        session.Account.Pronouns = pronouns;
        session.Account.Description = description;
        await db.SaveChangesAsync();
        await realtime.PublishAsync(session.AccountId, session.Account.AvatarRevision, db);
        return Results.Ok(ToDto(session.Account));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IridiumDbContext db, SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        db.AccountSessions.Remove(session);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> SecurityStatusAsync(
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        IRecoveryEmailSender emailSender)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        return Results.Ok(new AccountSecurityStatusDto(
            session.Account.RecoveryEmail is not null,
            MaskEmail(session.Account.RecoveryEmail),
            emailSender.IsConfigured));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        IAccountPasswordService passwords)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        if (passwords.VerifyPassword(session.Account, request.CurrentPassword) == AccountPasswordVerificationResult.Failed)
            return Results.BadRequest(new { message = "The current password is incorrect." });

        var errors = ValidateNewPassword(request.NewPassword, request.ConfirmNewPassword);
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        await using var transaction = await db.Database.BeginTransactionAsync();
        session.Account.PasswordHash = passwords.HashPassword(session.Account, request.NewPassword);
        await db.SaveChangesAsync();
        await db.AccountSessions
            .Where(value => value.AccountId == session.AccountId && value.Id != session.Id)
            .ExecuteDeleteAsync();
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateRecoveryEmailAsync(
        UpdateRecoveryEmailRequest request,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        IAccountPasswordService passwords,
        IRecoveryEmailSender emailSender)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();
        var verification = passwords.VerifyPassword(session.Account, request.CurrentPassword);
        if (verification == AccountPasswordVerificationResult.Failed)
            return Results.BadRequest(new { message = "The current password is incorrect." });

        if (!TryNormalizeEmail(request.RecoveryEmail, out var displayEmail, out var normalizedEmail))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.RecoveryEmail)] = ["Enter a valid recovery email address."]
            });

        session.Account.RecoveryEmail = displayEmail;
        session.Account.RecoveryEmailNormalized = normalizedEmail;
        if (verification == AccountPasswordVerificationResult.SuccessRehashNeeded)
            session.Account.PasswordHash = passwords.HashPassword(session.Account, request.CurrentPassword);
        await db.SaveChangesAsync();
        return Results.Ok(new AccountSecurityStatusDto(
            displayEmail is not null, MaskEmail(displayEmail), emailSender.IsConfigured));
    }

    private static async Task<IResult> RequestPasswordRecoveryAsync(
        PasswordRecoveryRequest request,
        HttpContext context,
        IridiumDbContext db,
        IRecoveryEmailSender emailSender,
        IOptions<NodeOptions> nodeOptions,
        IOptions<AccountSecurityOptions> securityOptions,
        TimeProvider timeProvider)
    {
        const string response = "If the account exists and has a recovery email configured, recovery instructions have been sent.";
        var username = request.Username?.Trim().ToLowerInvariant() ?? string.Empty;
        if (username.Length is < 1 or > 32 || !emailSender.IsConfigured)
            return Results.Ok(new PasswordRecoveryRequestResultDto(response));

        var account = await db.Accounts.SingleOrDefaultAsync(value => value.Username == username);
        if (account?.RecoveryEmail is null)
            return Results.Ok(new PasswordRecoveryRequestResultDto(response));

        var now = timeProvider.GetUtcNow();
        await db.PasswordRecoveryTokens
            .Where(value => value.ExpiresAt <= now || value.UsedAt != null)
            .ExecuteDeleteAsync();

        var token = PasswordRecoveryTokens.Create();
        db.PasswordRecoveryTokens.Add(new PasswordRecoveryToken
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Account = account,
            TokenHash = PasswordRecoveryTokens.Hash(token),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Math.Clamp(securityOptions.Value.RecoveryTokenMinutes, 5, 1440))
        });
        await db.SaveChangesAsync();

        var recoveryUri = RecoveryUri(context, nodeOptions.Value, token);
        await emailSender.SendPasswordRecoveryAsync(account.RecoveryEmail, recoveryUri, context.RequestAborted);
        return Results.Ok(new PasswordRecoveryRequestResultDto(response));
    }

    private static async Task<IResult> CompletePasswordRecoveryAsync(
        CompletePasswordRecoveryRequest request,
        IridiumDbContext db,
        IAccountPasswordService passwords,
        TimeProvider timeProvider)
    {
        var errors = ValidateNewPassword(request.NewPassword, request.ConfirmNewPassword);
        if (errors.Count != 0) return Results.ValidationProblem(errors);
        if (string.IsNullOrWhiteSpace(request.Token) ||
            request.Token.Length > AccountSecurityLimits.MaximumRecoveryTokenLength)
            return InvalidRecoveryToken();

        var tokenHash = PasswordRecoveryTokens.Hash(request.Token);
        var now = timeProvider.GetUtcNow();
        var recovery = await db.PasswordRecoveryTokens.Include(value => value.Account)
            .SingleOrDefaultAsync(value => value.TokenHash == tokenHash);
        if (recovery is null || recovery.UsedAt is not null || recovery.ExpiresAt <= now)
            return InvalidRecoveryToken();

        await using var transaction = await db.Database.BeginTransactionAsync();
        recovery.Account.PasswordHash = passwords.HashPassword(recovery.Account, request.NewPassword);
        var activeTokens = await db.PasswordRecoveryTokens
            .Where(value => value.AccountId == recovery.AccountId && value.UsedAt == null)
            .ToListAsync();
        foreach (var activeToken in activeTokens) activeToken.UsedAt = now;
        await db.SaveChangesAsync();
        await db.AccountSessions.Where(value => value.AccountId == recovery.AccountId).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static IResult InvalidRecoveryToken() =>
        Results.BadRequest(new { message = "This recovery link is invalid or has expired." });

    private static async Task<IResult> ValidatePasswordRecoveryAsync(
        ValidatePasswordRecoveryRequest request,
        IridiumDbContext db,
        TimeProvider timeProvider)
    {
        const string invalidMessage = "This recovery link is invalid or has expired.";
        if (string.IsNullOrWhiteSpace(request.Token) ||
            request.Token.Length > AccountSecurityLimits.MaximumRecoveryTokenLength)
            return Results.Ok(new PasswordRecoveryValidationResultDto(false, invalidMessage));
        var tokenHash = PasswordRecoveryTokens.Hash(request.Token);
        var now = timeProvider.GetUtcNow();
        var recovery = await db.PasswordRecoveryTokens.AsNoTracking()
            .SingleOrDefaultAsync(value => value.TokenHash == tokenHash);
        var valid = recovery is not null && recovery.UsedAt is null && recovery.ExpiresAt > now;
        return Results.Ok(new PasswordRecoveryValidationResultDto(valid,
            valid ? "This recovery link is ready." : invalidMessage));
    }

    private static Dictionary<string, string[]> ValidateNewPassword(string? password, string? confirmation)
    {
        var errors = new Dictionary<string, string[]>();
        if (password is null || password.Length is < AccountSecurityLimits.MinimumPasswordLength or
            > AccountSecurityLimits.MaximumPasswordLength)
            errors[nameof(password)] =
                [$"Passwords must be between {AccountSecurityLimits.MinimumPasswordLength} and {AccountSecurityLimits.MaximumPasswordLength} characters."];
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            errors[nameof(confirmation)] = ["The new passwords do not match."];
        return errors;
    }

    private static bool TryNormalizeEmail(string? input, out string? display, out string? normalized)
    {
        display = null;
        normalized = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        var candidate = input.Trim();
        if (candidate.Length > AccountSecurityLimits.MaximumRecoveryEmailLength ||
            candidate.Contains('<') || candidate.Contains('>')) return false;
        try
        {
            var address = new MailAddress(candidate);
            if (!string.Equals(address.Address, candidate, StringComparison.OrdinalIgnoreCase)) return false;
            display = $"{address.User}@{address.Host.ToLowerInvariant()}";
            normalized = display.ToLowerInvariant();
            return display.Length <= AccountSecurityLimits.MaximumRecoveryEmailLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var separator = email.LastIndexOf('@');
        if (separator <= 0 || separator == email.Length - 1) return "***";
        return $"{email[0]}***{email[separator..]}";
    }

    private static Uri RecoveryUri(HttpContext context, NodeOptions options, string token)
    {
        var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var baseAddress = Uri.TryCreate(options.PublicAuthority, UriKind.Absolute, out var configured) &&
                          (configured.Scheme == Uri.UriSchemeHttp || configured.Scheme == Uri.UriSchemeHttps)
            ? configured.GetLeftPart(UriPartial.Authority)
            : requestOrigin;
        return new Uri($"{baseAddress.TrimEnd('/')}/recover-password?token={Uri.EscapeDataString(token)}");
    }

    private static async Task<IResult> ListCommunitiesAsync(HttpContext context, IridiumDbContext db, SessionService sessions)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();

        var communities = await db.CommunityMembers
            .Where(value => value.AccountId == session.AccountId)
            .OrderBy(value => value.Community.Name)
            .Select(value => new CommunityDto(
                value.Community.Id,
                value.Community.Name,
                value.Community.Description,
                value.Community.OwnerAccountId,
                value.Community.CreatedAt, false, 0,
                value.Community.ActiveAvatarPresetId, value.Community.AvatarRevision,
                value.Community.ActiveBannerPresetId, value.Community.BannerRevision,
                value.Community.ActiveAvatarPresetId == null ? null : $"/api/communities/{value.Community.Id}/avatar?v={value.Community.AvatarRevision}",
                value.Community.ActiveBannerPresetId == null ? null : $"/api/communities/{value.Community.Id}/banner?v={value.Community.BannerRevision}",
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveAvatarPresetId)
                    .Select(preset => preset.CropX).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveAvatarPresetId)
                    .Select(preset => preset.CropY).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveAvatarPresetId)
                    .Select(preset => preset.Zoom).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveAvatarPresetId)
                    .Select(preset => preset.Width).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveAvatarPresetId)
                    .Select(preset => preset.Height).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveBannerPresetId)
                    .Select(preset => preset.CropX).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveBannerPresetId)
                    .Select(preset => preset.CropY).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveBannerPresetId)
                    .Select(preset => preset.Zoom).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveBannerPresetId)
                    .Select(preset => preset.Width).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveBannerPresetId)
                    .Select(preset => preset.Height).FirstOrDefault(),
                value.Community.MediaPresets.Where(preset => preset.Id == value.Community.ActiveBannerPresetId)
                    .Select(preset => preset.ProcessedObjectKey != null).FirstOrDefault()))
            .ToListAsync();
        for (var index = 0; index < communities.Count; index++)
        {
            var community = communities[index];
            var origin = $"{context.Request.Scheme}://{context.Request.Host}";
            community = community with
            {
                AvatarUrl = community.AvatarUrl is null ? null : origin + community.AvatarUrl,
                BannerUrl = community.BannerUrl is null ? null : origin + community.BannerUrl
            };
            var hasUnread = await db.ChannelMessages.AnyAsync(message =>
                message.CommunityId == community.Id && message.AuthorAccountId != session.AccountId &&
                !db.CommunityChannelReadStates.Any(state => state.CommunityId == message.CommunityId &&
                    state.ChannelId == message.ChannelId && state.AccountId == session.AccountId &&
                    state.LastReadAt >= message.CreatedAt));
            var mentionCount = await db.CommunityMentionNotifications.CountAsync(value =>
                value.AccountId == session.AccountId && value.CommunityId == community.Id && value.ReadAt == null);
            communities[index] = community with { HasUnread = hasUnread, MentionCount = mentionCount };
        }
        return Results.Ok(communities);
    }

    private static async Task<IResult> CreateCommunityAsync(
        CreateCommunityRequest request,
        HttpContext context,
        IridiumDbContext db,
        SessionService sessions,
        IOptions<NodeOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Iridium.Server.CommunityCreation");
        var stage = "Session";
        try
        {
            logger.LogInformation("COMMUNITY CREATE Request received");
            var session = await sessions.GetAsync(context, db);
            if (session is null)
            {
                logger.LogWarning("COMMUNITY CREATE FAILED Stage={Stage} Message=Unauthenticated request", stage);
                return Results.Unauthorized();
            }
            logger.LogInformation("COMMUNITY CREATE Authentication succeeded Account={AccountId}", session.AccountId);

            stage = "Validation";
            var name = request.Name.Trim();
            var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            var errors = new Dictionary<string, string[]>();
            if (name.Length is < 1 or > 100) errors[nameof(request.Name)] = ["Server names must be between 1 and 100 characters."];
            if (description?.Length > 500) errors[nameof(request.Description)] = ["Descriptions cannot exceed 500 characters."];
            if (errors.Count != 0)
            {
                logger.LogWarning("COMMUNITY CREATE FAILED Stage={Stage} Message=Validation rejected", stage);
                return Results.ValidationProblem(errors);
            }
            logger.LogInformation("COMMUNITY CREATE Validation succeeded Account={AccountId}", session.AccountId);

            stage = "OwnershipLimit";
            var ownedCount = await db.Communities.CountAsync(value => value.OwnerAccountId == session.AccountId);
            if (ownedCount >= options.Value.MaxCommunitiesPerUser)
            {
                logger.LogWarning("COMMUNITY CREATE FAILED Stage={Stage} Account={AccountId} Owned={Owned} Limit={Limit}",
                    stage, session.AccountId, ownedCount, options.Value.MaxCommunitiesPerUser);
                return Results.Problem(
                    $"This Node allows each account to own at most {options.Value.MaxCommunitiesPerUser} Servers.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            await using var transaction = await db.Database.BeginTransactionAsync();
            var now = DateTimeOffset.UtcNow;
            stage = "Community";
            var community = new Community { Id = Guid.NewGuid(), Name = name, Description = description,
                OwnerAccountId = session.AccountId, OwnerAccount = session.Account, CreatedAt = now };
            db.Communities.Add(community);
            logger.LogInformation("COMMUNITY CREATE Community row prepared Id={CommunityId}", community.Id);

            stage = "OwnerMembership";
            db.CommunityMembers.Add(new CommunityMember { CommunityId = community.Id, Community = community,
                AccountId = session.AccountId, Account = session.Account, JoinedAt = now });
            logger.LogInformation("COMMUNITY CREATE Owner membership prepared Id={CommunityId}", community.Id);

            stage = "DefaultRole";
            db.CommunityRoles.Add(new CommunityRole { Id = Guid.NewGuid(), CommunityId = community.Id,
                Community = community, Name = "@everyone", Position = 0, IsDefault = true,
                Permissions = CommunityPermission.ViewChannels | CommunityPermission.SendMessages |
                              CommunityPermission.ConnectVoice | CommunityPermission.SpeakVoice |
                              CommunityPermission.ShareScreen | CommunityPermission.ReadMessageHistory |
                              CommunityPermission.AttachFiles | CommunityPermission.EmbedLinks |
                              CommunityPermission.AddReactions | CommunityPermission.UseExternalEmoji |
                              CommunityPermission.CreateForumPosts | CommunityPermission.EmbedDocumentsInForumPosts });
            logger.LogInformation("COMMUNITY CREATE Default role prepared Id={CommunityId}", community.Id);

            stage = "DefaultCategory";
            var defaultCategory = new CommunityCategory { Id = Guid.NewGuid(), CommunityId = community.Id,
                Community = community, Name = "TEXT CHANNELS", Position = 0 };
            db.CommunityCategories.Add(defaultCategory);
            logger.LogInformation("COMMUNITY CREATE Default category prepared Id={CategoryId}", defaultCategory.Id);

            stage = "GeneralChannel";
            var defaultChannel = new CommunityChannel { Id = Guid.NewGuid(), CommunityId = community.Id,
                Community = community, CategoryId = defaultCategory.Id, Category = defaultCategory,
                Name = "general", Kind = CommunityChannelKind.Text, PermissionsSyncedToCategory = true,
                Position = 0, CreatedAt = now };
            db.CommunityChannels.Add(defaultChannel);
            logger.LogInformation("COMMUNITY CREATE General channel prepared Id={ChannelId}", defaultChannel.Id);

            stage = "Commit";
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            logger.LogInformation("COMMUNITY CREATE Transaction committed Id={CommunityId}", community.Id);

            logger.LogInformation("COMMUNITY CREATE Response success Id={CommunityId}", community.Id);
            return Results.Created($"/api/communities/{community.Id}", new CommunityDto(
                community.Id, community.Name, community.Description, community.OwnerAccountId, community.CreatedAt,
                AvatarRevision: community.AvatarRevision, BannerRevision: community.BannerRevision));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "COMMUNITY CREATE FAILED Stage={Stage} ExceptionType={ExceptionType} Message={Message}",
                stage, exception.GetType().Name, exception.Message);
            return Results.Problem("The Community could not be created. No changes were saved.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Dictionary<string, string[]> ValidateAccount(string username, string displayName, string password)
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedUsername = username.Trim();
        if (!UsernamePattern().IsMatch(normalizedUsername))
            errors[nameof(username)] = ["Usernames must be 1-32 characters using letters, numbers, dots, underscores, or hyphens."];
        if (displayName.Trim().Length is < 1 or > 64)
            errors[nameof(displayName)] = ["Display names must be between 1 and 64 characters."];
        if (password.Length is < AccountSecurityLimits.MinimumPasswordLength or
            > AccountSecurityLimits.MaximumPasswordLength)
            errors[nameof(password)] =
                [$"Passwords must be between {AccountSecurityLimits.MinimumPasswordLength} and {AccountSecurityLimits.MaximumPasswordLength} characters."];
        return errors;
    }

    private static NodeAccountDto ToDto(NodeAccount account) =>
        new(account.Id, account.Username, account.DisplayName, account.Pronouns, account.Description,
            account.PreferredPresence, account.CreatedAt, account.ActiveAvatarPresetId, account.AvatarRevision,
            account.ActiveBannerPresetId, account.BannerRevision, account.BaseAvatarPresetId);

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
