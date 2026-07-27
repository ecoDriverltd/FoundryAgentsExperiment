using FoundryAgentsExperiment.Web.Client.Services;
using FoundryAgentsExperiment.Web.Components;
using Microsoft.Agents.AI.AGUI;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddHttpForwarderWithServiceDiscovery();

// Register WASM client services on the server so Blazor pre-render can inject them.
// RendererInfo.IsInteractive guards prevent any browser-only (localStorage) calls during pre-render.
// ChatClientAgent is built per chat-page instance (not here) so it can be given frontend tools
// that close over page-scoped, JS-interop-backed services like GeolocationService.
builder.Services.AddHttpClient<AGUIChatClient>()
    .AddTypedClient<AGUIChatClient>((http, _) => new AGUIChatClient(http, "/ag-ui"));
builder.Services.AddScoped<UserIdentityService>();
builder.Services.AddScoped<GeolocationService>();
builder.Services.AddScoped<ConversationStore>();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FoundryAgentsExperiment.Web.Client._Imports).Assembly);

var agentUrl = builder.Configuration["services:agent-dotnet:https:0"]
    ?? throw new InvalidOperationException("Agent service URL not configured. Ensure WithReference(agent) is set in AppHost.");

//Forward / ag-ui straight to the agent host — SSE streams through unbuffered
app.MapForwarder("/ag-ui", agentUrl,
    new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(5) },
    transformContext =>
    {
        // Inject user identity so the agent host knows who's calling
        // In production, derive this from your auth claims
        transformContext.AddRequestTransform(ctx =>
        {
            // Client shouldn't sent this header, this is a back/end server concern.
            // The user id can then come from cookie auth/back end.
            ctx.ProxyRequest.Headers.Remove("x-agent-user-id");

            var userId = ctx.HttpContext.User?.Identity?.Name ?? "anonymous";
            ctx.ProxyRequest.Headers.TryAddWithoutValidation("x-agent-user-id", userId);
            return ValueTask.CompletedTask;
        });
    });

app.Run();
