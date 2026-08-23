using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Iridium.Web;
using Iridium.Client.Core;
using Iridium.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// TODO: Remove temporary voice-call diagnostics once WebRTC calls are stable.
if (builder.HostEnvironment.IsDevelopment())
{
    builder.Logging.AddFilter("Iridium.Client.Core.CallClientService", LogLevel.Debug);
    builder.Logging.AddFilter("Iridium.Web.Services.WebRtcCallMediaService", LogLevel.Debug);
}

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BrowserClientStorage>();
builder.Services.AddScoped<AppearanceService>();
builder.Services.AddScoped<MessageMenuCoordinator>();
builder.Services.AddScoped<EmojiDetailPopupCoordinator>();
builder.Services.AddScoped<UiSoundService>();
builder.Services.AddScoped<IAttachmentComposerService, AttachmentComposerService>();
builder.Services.AddScoped<ISavedNodeStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<INodeTokenStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<ISavedAccountStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<IActiveAccountSelectionStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<IVoiceParticipantPreferenceStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<VoiceParticipantPreferencesService>();
builder.Services.AddScoped<IEmojiPickerPreferenceStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<EmojiPickerPreferencesService>();
builder.Services.AddScoped<ProfileMediaService>();
builder.Services.AddScoped<CommunityEmojiService>();
builder.Services.AddScoped<SavedNodeState>();
builder.Services.AddScoped<NodeSession>();
builder.Services.AddScoped<IWebRtcConfigurationSource>(sp => sp.GetRequiredService<NodeSession>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IWebRtcConfigurationProvider, WebRtcConfigurationProvider>();
builder.Services.AddScoped<RealtimeConnectionService>();
builder.Services.AddScoped<CommunitySession>();
builder.Services.AddScoped<ChannelMessagingSession>();
builder.Services.AddScoped<ICallMediaService, WebRtcCallMediaService>();
builder.Services.AddScoped<ICommunityVoiceMediaClient, BrowserCommunityVoiceMediaClient>();
builder.Services.AddScoped<CallClientService>();
builder.Services.AddScoped<CommunityVoiceSession>();
builder.Services.AddScoped<IDirectVoiceSession>(sp => sp.GetRequiredService<CallClientService>());
builder.Services.AddScoped<ICommunityVoiceControlSession>(sp => sp.GetRequiredService<CommunityVoiceSession>());
builder.Services.AddScoped<ActiveVoiceSessionCoordinator>();
builder.Services.AddScoped<AccountSwitchService>();
builder.Services.AddScoped<IIdentityProfileResolver, SameNodeIdentityProfileResolver>();
builder.Services.AddScoped<ICommunityInviteResolver, CommunityInviteResolver>();
builder.Services.AddScoped<ICategoryCollapseStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<ILastCommunityChannelStore>(sp => sp.GetRequiredService<BrowserClientStorage>());

await builder.Build().RunAsync();
