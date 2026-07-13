using FoundryAgentsExperiment.Web.Client.Services;
using Microsoft.Agents.AI.AGUI;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.AI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// In Blazor WASM, AddHttpClient uses BrowserHttpHandler (browser fetch) under the hood.
// IHttpClientFactory manages handler lifetimes correctly regardless of environment.
builder.Services.AddHttpClient<IChatClient>("ag-ui", client =>
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddTypedClient<IChatClient>((http, _) => new AGUIChatClient(http, "/ag-ui"));

builder.Services.AddScoped<UserIdentityService>();
builder.Services.AddScoped<ConversationStore>();
builder.Services.AddScoped<AgentChatService>();

await builder.Build().RunAsync();
