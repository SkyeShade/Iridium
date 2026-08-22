using System.Text.RegularExpressions;
using Iridium.Protocol;
using Iridium.Server.Configuration;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Iridium.Server.Profiles;

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

        var communities = endpoints.MapGroup("/api/communities");
        communities.MapGet("/", ListCommunitiesAsync);
        communities.MapPost("/", CreateCommunityAsync);
        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterAccountRequest request,
        IridiumDbContext db,
        IPasswordHasher<NodeAccount> passwords,
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
        IPasswordHasher<NodeAccount> passwords,
        SessionService sessions)
    {
        var username = request.Username.Trim().ToLowerInvariant();
        var account = await db.Accounts.SingleOrDefaultAsync(value => value.Username == username);
        if (account is null ||
            passwords.VerifyHashedPassword(account, account.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Results.Unauthorized();

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
                value.Community.CreatedAt))
            .ToListAsync();
        for (var index = 0; index < communities.Count; index++)
        {
            var community = communities[index];
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
        IOptions<NodeOptions> options)
    {
        var session = await sessions.GetAsync(context, db);
        if (session is null) return Results.Unauthorized();

        var name = request.Name.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var errors = new Dictionary<string, string[]>();
        if (name.Length is < 1 or > 100) errors[nameof(request.Name)] = ["Community names must be between 1 and 100 characters."];
        if (description?.Length > 500) errors[nameof(request.Description)] = ["Descriptions cannot exceed 500 characters."];
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        var ownedCount = await db.Communities.CountAsync(value => value.OwnerAccountId == session.AccountId);
        if (ownedCount >= options.Value.MaxCommunitiesPerUser)
            return Results.Problem(
                $"This node allows each account to own at most {options.Value.MaxCommunitiesPerUser} communities.",
                statusCode: StatusCodes.Status409Conflict);

        var now = DateTimeOffset.UtcNow;
        var community = new Community
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            OwnerAccountId = session.AccountId,
            OwnerAccount = session.Account,
            CreatedAt = now
        };
        var membership = new CommunityMember
        {
            CommunityId = community.Id,
            Community = community,
            AccountId = session.AccountId,
            Account = session.Account,
            JoinedAt = now
        };
        var defaultRole = new CommunityRole
        {
            Id = Guid.NewGuid(),
            CommunityId = community.Id,
            Community = community,
            Name = "@everyone",
            Position = 0,
            IsDefault = true,
            Permissions = CommunityPermission.ViewChannels | CommunityPermission.SendMessages |
                          CommunityPermission.ConnectVoice | CommunityPermission.SpeakVoice
        };
        var defaultCategory = new CommunityCategory
        {
            Id = Guid.NewGuid(),
            CommunityId = community.Id,
            Community = community,
            Name = "TEXT CHANNELS",
            Position = 0
        };
        var defaultChannel = new CommunityChannel
        {
            Id = Guid.NewGuid(),
            CommunityId = community.Id,
            Community = community,
            CategoryId = defaultCategory.Id,
            Category = defaultCategory,
            Name = "general",
            Position = 0,
            CreatedAt = now
        };

        db.Communities.Add(community);
        db.CommunityMembers.Add(membership);
        db.CommunityRoles.Add(defaultRole);
        db.CommunityCategories.Add(defaultCategory);
        db.CommunityChannels.Add(defaultChannel);
        await db.SaveChangesAsync();

        return Results.Created($"/api/communities/{community.Id}", new CommunityDto(
            community.Id, community.Name, community.Description, community.OwnerAccountId, community.CreatedAt));
    }

    private static Dictionary<string, string[]> ValidateAccount(string username, string displayName, string password)
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedUsername = username.Trim();
        if (!UsernamePattern().IsMatch(normalizedUsername))
            errors[nameof(username)] = ["Usernames must be 3-32 characters using letters, numbers, dots, underscores, or hyphens."];
        if (displayName.Trim().Length is < 1 or > 64)
            errors[nameof(displayName)] = ["Display names must be between 1 and 64 characters."];
        if (password.Length is < 8 or > 256)
            errors[nameof(password)] = ["Passwords must be between 8 and 256 characters."];
        return errors;
    }

    private static NodeAccountDto ToDto(NodeAccount account) =>
        new(account.Id, account.Username, account.DisplayName, account.Pronouns, account.Description,
            account.PreferredPresence, account.CreatedAt, account.ActiveAvatarPresetId, account.AvatarRevision,
            account.ActiveBannerPresetId, account.BannerRevision);

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
