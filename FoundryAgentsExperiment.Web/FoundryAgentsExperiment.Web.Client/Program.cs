using FoundryAgentsExperiment.Web.Client.Services;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.AI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddScoped<UserIdentityService>();

// In Blazor WASM, AddHttpClient uses BrowserHttpHandler (browser fetch) under the hood.
// IHttpClientFactory manages handler lifetimes correctly regardless of environment.
// User identity is a server-side concern: the Web host's YARP transform derives x-agent-user-id
// from the authenticated request, not from anything the client sends.
builder.Services.AddHttpClient<ChatClientAgent>("ag-ui", client =>
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddTypedClient<ChatClientAgent>((http, _) =>
        new AGUIChatClient(http, "/ag-ui").AsAIAgent(name: "agui-client", description: "AG-UI Client Agent"));

builder.Services.AddScoped<ConversationStore>();
builder.Services.AddScoped<AgentChatService>();

await builder.Build().RunAsync();
