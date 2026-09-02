using FoundryAgentsExperiment.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped<UserIdentityService>();

// In Blazor WASM, AddHttpClient uses BrowserHttpHandler (browser fetch) under the hood.
// IHttpClientFactory manages handler lifetimes correctly regardless of environment.
// User identity is a server-side concern: the Web host's YARP transform derives x-agent-user-id
// from the authenticated request, not from anything the client sends.
builder.Services.AddHttpClient<ResponsesChatClient>(client =>
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));

builder.Services.AddHttpClient<AgentConversationClient>(client =>
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));

await builder.Build().RunAsync();
