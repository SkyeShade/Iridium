using Iridium.Protocol;
using Iridium.Server;
using Iridium.Server.Api;
using Iridium.Server.Configuration;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;
using Iridium.Server.Storage;
using Iridium.Server.Calls;
using Iridium.Server.Voice;
using Iridium.Server.Communities;
using Iridium.Server.Profiles;
using Iridium.Server.Messages;
using Iridium.Server.Embeds;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
DeploymentConfiguration.AddExternalConfiguration(builder.Configuration, builder.Environment);
var configuredNodeOptions = builder.Configuration.GetSection(NodeOptions.SectionName).Get<NodeOptions>() ?? new NodeOptions();
var maximumUploadRequestBytes = checked(configuredNodeOptions.MaxAttachmentBytes + 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maximumUploadRequestBytes);

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ConnectionCounter>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<VoiceConnectionRegistry>();
builder.Services.AddSingleton<VoiceTraceLogger>();
builder.Services.AddSingleton<CommunityRevisionTracker>();
builder.Services.AddSingleton<CommunityRealtimePublisher>();
builder.Services.AddScoped<CommunityVoicePermissionEnforcer>();
builder.Services.AddSingleton<ProfileRealtimePublisher>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GoogleDocsDocumentParser>();
builder.Services.AddHttpClient<IGoogleDocsPublishedDocumentService, GoogleDocsPublishedDocumentService>()
    .ConfigureHttpClient(client =>
    {
        // Source and media requests use separate linked cancellation windows in the provider.
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.MaxResponseContentBufferSize = GoogleDocsPublishedDocumentService.MaximumResponseBytes;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
builder.Services.Configure<NodeOptions>(builder.Configuration.GetSection(NodeOptions.SectionName));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.Configure<AccountSecurityOptions>(builder.Configuration.GetSection(AccountSecurityOptions.SectionName));
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.SectionName));
builder.Services.Configure<WebRtcOptions>(builder.Configuration.GetSection(WebRtcOptions.SectionName));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = maximumUploadRequestBytes);
builder.Services.AddSingleton<ICommunityLimitsService, CommunityLimitsService>();
builder.Services.AddDbContext<IridiumDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Iridium")));
builder.Services.AddSingleton<IAccountPasswordService, AccountPasswordService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddSingleton<IRecoveryEmailSender, SmtpRecoveryEmailSender>();
builder.Services.AddRateLimiter(options => options.AddPolicy("password-recovery", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
            AutoReplenishment = true
        })));
builder.Services.AddScoped<CommunityAuthorizationService>();
builder.Services.AddScoped<HistoricalAuthorPresentationService>();
builder.Services.AddScoped<MessageReactionService>();
builder.Services.AddScoped<CommunityInviteService>();
builder.Services.AddSingleton<IAttachmentStorage, LocalAttachmentStorage>();
builder.Services.AddSingleton<IImagePreviewGenerator, ImagePreviewGenerator>();
builder.Services.AddSingleton<IAttachmentMediaTypeValidator, AttachmentMediaTypeValidator>();
builder.Services.AddSingleton<IAttachmentPlaybackTokenService, AttachmentPlaybackTokenService>();
builder.Services.AddSingleton<IAvatarImageValidator, AvatarImageValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICallService, CallService>();
builder.Services.AddSingleton<INodeMediaSessionService, LiveKitMediaSessionService>();
if (builder.Environment.IsDevelopment() &&
    builder.Configuration.GetValue<MediaProvider>("Media:Provider") == MediaProvider.DevelopmentPeerToPeer)
    builder.Services.AddSingleton<IMediaService, DirectWebRtcMediaService>();
else
    builder.Services.AddSingleton<IMediaService, UnavailablePeerMediaService>();
builder.Services.AddSingleton<IWebRtcIceConfigurationService, WebRtcIceConfigurationService>();
if (builder.Environment.IsDevelopment() &&
    builder.Configuration.GetValue<bool>("Media:EnableDevelopmentCommunityPeerMesh"))
    builder.Services.AddSingleton<ICommunityVoiceMediaGateway, DevelopmentPeerMeshCommunityVoiceMediaGateway>();
else if (builder.Configuration.GetValue<MediaProvider>("Media:Provider") == MediaProvider.LiveKit)
    builder.Services.AddSingleton<ICommunityVoiceMediaGateway, LiveKitCommunityVoiceMediaGateway>();
else
    builder.Services.AddSingleton<ICommunityVoiceMediaGateway, UnavailableCommunityVoiceMediaGateway>();
builder.Services.AddSingleton<CommunityVoiceRoomService>();
builder.Services.AddSingleton<VoiceStreamRegistry>();
builder.Services.AddScoped<DirectCallAuthorizationService>();
builder.Services.AddHostedService<CallTimeoutService>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddPolicy("DevelopmentClient", policy =>
        policy.WithOrigins("http://localhost:5185", "https://localhost:7027")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));
}

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IridiumDbContext>();
    await db.Database.EnsureCreatedAsync();
    await DatabaseCompatibility.EnsureAccountSessionActivityColumnAsync(db);
    await DatabaseCompatibility.EnsureAccountSecuritySchemaAsync(db);
    await DatabaseCompatibility.EnsurePronounsColumnAsync(db);
    await DatabaseCompatibility.EnsurePresenceColumnAsync(db);
    await DatabaseCompatibility.EnsureFriendsTableAsync(db);
    await DatabaseCompatibility.EnsureAccountBlocksAsync(db);
    await DatabaseCompatibility.EnsureEarlyCommunitySchemaAsync(db);
    await DatabaseCompatibility.EnsureUnifiedCommunitySidebarOrderingAsync(db);
    await DatabaseCompatibility.EnsureChannelMessagesTableAsync(db);
    await DatabaseCompatibility.EnsureCommunityChannelReadStatesAsync(db);
    await DatabaseCompatibility.EnsureCommunityMentionNotificationsAsync(db);
    await DatabaseCompatibility.EnsureDirectMessageTablesAsync(db);
    await DatabaseCompatibility.EnsureMessageClientIdsAsync(db);
    await DatabaseCompatibility.EnsureMessageHistoryIndexesAsync(db);
    await DatabaseCompatibility.EnsureCommunityManagementSchemaAsync(db);
    await DatabaseCompatibility.EnsureCommunityForumPermissionDefaultsAsync(db);
    await DatabaseCompatibility.EnsureCommunityVoiceSchemaAsync(db);
    await DatabaseCompatibility.EnsureCommunityPermissionOverwriteSchemaAsync(db);
    await DatabaseCompatibility.EnsureAvatarPresetSchemaAsync(db);
    await DatabaseCompatibility.EnsureBannerPresetSchemaAsync(db);
await DatabaseCompatibility.EnsureCommunityMediaSchemaAsync(db);
await DatabaseCompatibility.EnsureCommunityEmojiSchemaAsync(db);
    await DatabaseCompatibility.EnsureMessageReactionSchemaAsync(db);
    await DatabaseCompatibility.EnsureAttachmentsTableAsync(db);
    await DatabaseCompatibility.EnsureMessageForwardingSchemaAsync(db);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (builder.Configuration.GetValue("Deployment:UseHttpsRedirection", true))
    app.UseWhen(context => !context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase),
        branch => branch.UseHttpsRedirection());
if (app.Environment.IsDevelopment())
{
    app.UseCors("DevelopmentClient");
}
app.UseRateLimiter();

app.MapGet("/api/server-info", (ConnectionCounter connections, ICommunityLimitsService limitService,
        INodeMediaSessionService nodeMedia) =>
    {
        var limits = limitService.GetEffectiveLimits();
        return
        new ServerInfoDto(
            "Iridium Server",
            "A self-hosted Iridium chat server",
            ProtocolVersion.Current,
            connections.Count,
            null,
            null,
            limits.MaxAttachmentBytes,
            limits.MaxAttachmentsPerMessage,
            limits.MaxMessageCharacters,
            nodeMedia.Enabled,
            nodeMedia.Enabled);
    })
    .WithName("GetServerInfo");

app.MapGet("/health", () => Results.Text("Healthy\n", "text/plain"))
    .AllowAnonymous()
    .WithName("GetHealth");

app.MapHub<ChatHub>("/hubs/chat");
app.MapAccountEndpoints();
app.MapFriendEndpoints();
app.MapCommunityStructureEndpoints();
app.MapCommunityForumEndpoints();
app.MapCommunityForumTagEndpoints();
app.MapMessageEndpoints();
app.MapDirectMessageEndpoints();
app.MapCommunityManagementEndpoints();
app.MapAttachmentEndpoints();
app.MapAvatarPresetEndpoints();
app.MapProfilePresetEndpoints();
app.MapBannerPresetEndpoints();
app.MapCommunityMediaEndpoints();
app.MapCommunityEmojiEndpoints();
app.MapWebRtcEndpoints();

app.Run();
