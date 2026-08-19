using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Iridium.Web;
using Iridium.Client.Core;
using Iridium.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BrowserClientStorage>();
builder.Services.AddScoped<AppearanceService>();
builder.Services.AddScoped<MessageMenuCoordinator>();
builder.Services.AddScoped<ISavedNodeStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<INodeTokenStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<ISavedAccountStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<IActiveAccountSelectionStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<SavedNodeState>();
builder.Services.AddScoped<NodeSession>();
builder.Services.AddScoped<CommunitySession>();
builder.Services.AddScoped<ChannelMessagingSession>();
builder.Services.AddScoped<AccountSwitchService>();
builder.Services.AddScoped<IIdentityProfileResolver, SameNodeIdentityProfileResolver>();
builder.Services.AddScoped<ICommunityInviteResolver, CommunityInviteResolver>();
builder.Services.AddScoped<ICategoryCollapseStore>(sp => sp.GetRequiredService<BrowserClientStorage>());
builder.Services.AddScoped<ILastCommunityChannelStore>(sp => sp.GetRequiredService<BrowserClientStorage>());

await builder.Build().RunAsync();
