using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using SimpleAgent;
using System.Text.Json;

var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var builder = WebApplication.CreateBuilder(args);

var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);
//AgentHostBuilder builder = AgentHost.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS")))
{
    builder.WebHost.UseUrls($"http://+:{port}", $"https://+:{port}");
}

// OLTP errors with this and AgentHostBuilder, so commenting out for now. Need to investigate further.
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.AddServiceDefaults();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

//builder.AddOpenAIResponses();
//builder.AddOpenAIConversations();

Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

string agentName = "agent-dotnet";
var projectClient = new AIProjectClient(foundrySettings.ProjectUri, foundrySettings.GetCredential(builder.Environment));
var openAIClient = projectClient.GetProjectOpenAIClient();

// Shared instance so both the memory provider (constructed before the host is built) and
// FoundryBackedAgentSessionStore (resolved from DI at request time) see the same request context.
var httpContextAccessor = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);

// NOTE: Microsoft.Agents.AI.Foundry.Hosting.InMemoryAgentSessionStore implements a DIFFERENT
// AgentSessionStore contract (Foundry-specific, used by MapFoundryResponses/MapOpenAIConversations)
// than the one MapAGUI resolves (Microsoft.Agents.AI.Hosting.AgentSessionStore). Registering that
// type here was silently ignored by MapAGUI - use the AG-UI-compatible store instead.
builder.Services.AddFoundryBackedAgentSessionStore(agentName);

// Foundry-managed memory: FoundryMemoryProvider is an AIContextProvider, so it plugs straight into
// ChatClientAgentOptions.AIContextProviders. Foundry runs the chat/embedding models for extraction and
// search itself - we only need to name the deployments, not wire up an embedding client ourselves.
// Memories are scoped per caller using the same x-agent-user-id header convention as
// FoundryBackedAgentSessionStore (not the unrelated x-memory-user-id header used by the hosted
// memory-search *tool* on versioned Foundry agents).

//var memoryAsTool = new MemorySearchPreviewTool()

// FoundryMemoryProvider is constructed before builder.Build(), so DI's ILoggerFactory isn't
// available yet. Without an explicit loggerFactory, every internal LogInformation/LogError call
// (including the catch block around the memory-update request) is a silent no-op, which made
// write failures invisible. Give it a standalone console logger so we can actually see what's
// happening.
using var memoryProviderLoggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Trace);
});

var memoryProvider = new FoundryMemoryProvider(
    projectClient,
    options: new FoundryMemoryProviderOptions
    {
        EnableSensitiveTelemetryData = true
    },
    memoryStoreName: $"{agentName}-memory",
    stateInitializer: _ => new FoundryMemoryProvider.State(
        new FoundryMemoryProviderScope(GetAgentUserId(httpContextAccessor))),
    loggerFactory: memoryProviderLoggerFactory);

await memoryProvider.EnsureMemoryStoreCreatedAsync(
    chatModel: foundrySettings.DeploymentName,
    embeddingModel: foundrySettings.EmbeddingDeploymentName,
    description: $"Memory store for {agentName}");

AIAgent agent = projectClient
    .AsAIAgent(new ChatClientAgentOptions
    {
        ChatOptions = new()
        {
            Instructions = "You are a helpful assistant.",
            ModelId = foundrySettings.DeploymentName,
            //Tools = []
        },
        Name = agentName,
        AIContextProviders = [memoryProvider],
    });

// NOTE: agent is shared by both /v1 (AddFoundryResponses) and /ag-ui (MapAGUI via this keyed
// singleton). Do NOT set ChatHistoryProvider on it. Foundry's projectClient returns a
// service-managed ConversationId on every response, and ChatClientAgent.UpdateSessionConversationId
// throws InvalidOperationException ("Only ConversationId or ChatHistoryProvider may be used, but
// not both") if a ChatHistoryProvider is also explicitly configured. That exception previously
// aborted every AG-UI streaming response mid-turn, which meant AgentSessionStore.SaveSessionAsync
// was never reached - the session store stayed empty and each turn silently started a fresh session.
// I suspect you could do something with 'mode 2' if you wanted your own ChatHistoryProvider
// https://devblogs.microsoft.com/agent-framework/chat-history-storage-patterns-in-microsoft-agent-framework/#fixed-mode-providers
builder.Services.AddKeyedSingleton(agentName, agent);

builder.Services.AddFoundryResponses(agent);
builder.Services.AddFoundryToolboxes(foundrySettings.GetCredential(builder.Environment));

// This says for dev/test doing in memory, so I guess you need to register something more permanent for production.
builder.Services.AddOpenAIConversations();

var agentHost = builder.Build();

// These two seem to let things work in DevUI
agentHost.MapFoundryResponses("/v1");
agentHost.MapOpenAIConversations();

// Checkpoint 1: log the raw wire payload for every /ag-ui call BEFORE MapAGUI's handler runs,
// so we can see exactly what ThreadId and message count the client actually sent, per turn.
agentHost.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/ag-ui"))
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;

        var userIdHeader = context.Request.Headers["x-agent-user-id"].ToString();

        var log = context.RequestServices.GetRequiredService<ILogger<Program>>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var threadId = doc.RootElement.TryGetProperty("threadId", out var t) ? t.GetString() : "(none)";
            var messageCount = doc.RootElement.TryGetProperty("messages", out var m) ? m.GetArrayLength() : -1;
            log.LogInformation("[Wire] POST /ag-ui threadId={ThreadId} messageCount={MessageCount} userId={UserId}", threadId, messageCount, userIdHeader);

            // TODO: check we get the user id as well? For memory.
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "[Wire] POST /ag-ui - failed to parse body for logging");
        }
    }

    await next();
});

// Hosted agent can work with conversations via AG-UI but needs a session store.
// Not a 'Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore' but a 'Microsoft.Agents.AI.Hosting.AgentSessionStore' (obviously!)
// The FoundryBackedAgentSessionStore adapts the Foundry-specific store to the AG-UI-compatible contract.
agentHost.MapAGUIServer(agentName, "/ag-ui");

await agentHost.RunAsync();

// Same convention as FoundryBackedAgentSessionStore.GetUserId - the Foundry platform normally
// injects x-agent-user-id on every inbound request (see FoundrySettings.UseFoundryLocalUserIdFallback
// for the local-dev fallback).
static string GetAgentUserId(IHttpContextAccessor httpContextAccessor)
{
    string? userId = httpContextAccessor.HttpContext?.Request.Headers["x-agent-user-id"].ToString();

    if (string.IsNullOrEmpty(userId))
    {
        return "annonymous"; // Fallback for local dev if Foundry doesn't inject the header
    }

    return userId;
}