using FoundryAgentsExperiment.Web.Client.Services;
using FoundryAgentsExperiment.Web.Components;
using FoundryAgentsExperiment.Web;
using Microsoft.AspNetCore.Authentication;
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
// RendererInfo.IsInteractive guards prevent browser-only calls during pre-render.
builder.Services.AddHttpClient<ResponsesChatClient>();
builder.Services.AddHttpClient<AgentConversationClient>();
builder.Services.AddScoped<UserIdentityService>();

if (builder.Environment.IsDevelopment())
{
    builder.Services
        .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName,
            _ => { });
}

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

app.UseAuthentication();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(FoundryAgentsExperiment.Web.Client._Imports).Assembly);

var agentUrl = builder.Configuration["services:agent-dotnet:https:0"]
    ?? throw new InvalidOperationException("Agent service URL not configured. Ensure WithReference(agent) is set in AppHost.");

// Forward standard Foundry Responses SSE events without buffering.
app.MapForwarder("/v1/{**catch-all}", agentUrl,
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

            var userId = ctx.HttpContext.User.Identity?.Name
                ?? throw new InvalidOperationException("An authenticated user is required to call the agent.");
            ctx.ProxyRequest.Headers.TryAddWithoutValidation("x-agent-user-id", userId);
            return ValueTask.CompletedTask;
        });
    });

app.MapForwarder("/agent/{**catch-all}", agentUrl,
    new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromMinutes(1) },
    transformContext =>
    {
        transformContext.AddPathRemovePrefix("/agent");
        transformContext.AddRequestTransform(ctx =>
        {
            ctx.ProxyRequest.Headers.Remove("x-agent-user-id");

            var userId = ctx.HttpContext.User.Identity?.Name
                ?? throw new InvalidOperationException("An authenticated user is required to call the agent.");
            ctx.ProxyRequest.Headers.TryAddWithoutValidation("x-agent-user-id", userId);
            return ValueTask.CompletedTask;
        });
    });

app.Run();
