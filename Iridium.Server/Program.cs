using Iridium.Protocol;
using Iridium.Server;
using Iridium.Server.Api;
using Iridium.Server.Configuration;
using Iridium.Server.Hubs;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Iridium.Server.Domain;
using Iridium.Server.Storage;
using Iridium.Server.Calls;

var builder = WebApplication.CreateBuilder(args);
var configuredNodeOptions = builder.Configuration.GetSection(NodeOptions.SectionName).Get<NodeOptions>() ?? new NodeOptions();
var maximumUploadRequestBytes = checked(configuredNodeOptions.MaxAttachmentBytes + 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maximumUploadRequestBytes);

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ConnectionCounter>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.Configure<NodeOptions>(builder.Configuration.GetSection(NodeOptions.SectionName));
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.SectionName));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = maximumUploadRequestBytes);
builder.Services.AddSingleton<ICommunityLimitsService, CommunityLimitsService>();
builder.Services.AddDbContext<IridiumDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Iridium")));
builder.Services.AddSingleton<IPasswordHasher<NodeAccount>, PasswordHasher<NodeAccount>>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<CommunityAuthorizationService>();
builder.Services.AddScoped<CommunityInviteService>();
builder.Services.AddSingleton<IAttachmentStorage, LocalAttachmentStorage>();
builder.Services.AddSingleton<IImagePreviewGenerator, ImagePreviewGenerator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICallService, CallService>();
builder.Services.AddSingleton<IMediaService, DirectWebRtcMediaService>();
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
    await DatabaseCompatibility.EnsurePronounsColumnAsync(db);
    await DatabaseCompatibility.EnsurePresenceColumnAsync(db);
    await DatabaseCompatibility.EnsureFriendsTableAsync(db);
    await DatabaseCompatibility.EnsureAccountBlocksAsync(db);
    await DatabaseCompatibility.EnsureCommunityStructureTablesAsync(db);
    await DatabaseCompatibility.EnsureUnifiedCommunitySidebarOrderingAsync(db);
    await DatabaseCompatibility.EnsureChannelMessagesTableAsync(db);
    await DatabaseCompatibility.EnsureCommunityChannelReadStatesAsync(db);
    await DatabaseCompatibility.EnsureCommunityMentionNotificationsAsync(db);
    await DatabaseCompatibility.EnsureDirectMessageTablesAsync(db);
    await DatabaseCompatibility.EnsureMessageClientIdsAsync(db);
    await DatabaseCompatibility.EnsureMessageHistoryIndexesAsync(db);
    await DatabaseCompatibility.EnsureCommunityManagementSchemaAsync(db);
    await DatabaseCompatibility.EnsureAttachmentsTableAsync(db);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
if (app.Environment.IsDevelopment())
{
    app.UseCors("DevelopmentClient");
}

app.MapGet("/api/server-info", (ConnectionCounter connections, ICommunityLimitsService limitService) =>
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
            limits.MaxMessageCharacters);
    })
    .WithName("GetServerInfo");

app.MapHub<ChatHub>("/hubs/chat");
app.MapAccountEndpoints();
app.MapFriendEndpoints();
app.MapCommunityStructureEndpoints();
app.MapMessageEndpoints();
app.MapDirectMessageEndpoints();
app.MapCommunityManagementEndpoints();
app.MapAttachmentEndpoints();

app.Run();
