using FoundryAgentsExperiment.Web.Client.Services;
using Microsoft.Agents.AI.AGUI;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped<UserIdentityService>();
builder.Services.AddScoped<GeolocationService>();

// In Blazor WASM, AddHttpClient uses BrowserHttpHandler (browser fetch) under the hood.
// IHttpClientFactory manages handler lifetimes correctly regardless of environment.
// User identity is a server-side concern: the Web host's YARP transform derives x-agent-user-id
// from the authenticated request, not from anything the client sends.
// ChatClientAgent is built per chat-page instance (not here) so it can be given frontend tools
// that close over page-scoped, JS-interop-backed services like GeolocationService.
builder.Services.AddHttpClient<AGUIChatClient>("ag-ui", client =>
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddTypedClient<AGUIChatClient>((http, _) => new AGUIChatClient(http, "/ag-ui"));

builder.Services.AddScoped<ConversationStore>();

await builder.Build().RunAsync();
