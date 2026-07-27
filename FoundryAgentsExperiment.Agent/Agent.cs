using Azure.AI.Projects;
using Azure.Core;
using FoundryAgentsExperiment.Agent;
using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using SimpleAgent;
using System.Text.Json;

var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var builder = WebApplication.CreateBuilder(args);

var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);
TokenCredential credential = FoundrySettings.GetCredential(builder.Environment);
//AgentHostBuilder builder = AgentHost.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS")))
{
    builder.WebHost.UseUrls($"http://+:{port}", $"https://+:{port}");
}

// OLTP errors with this and AgentHostBuilder, so using WebApplicationBuilder instead.
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

Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

string agentName = "agent-dotnet";
var projectClient = new AIProjectClient(foundrySettings.ProjectUri, credential);
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
var memoryProvider = await projectClient.GetFoundryMemoryProviderAsync(agentName, httpContextAccessor, foundrySettings);

// Create a POC toolbox/skill/mcp skill provider for the agent to use. The Foundry MCP server handles skill invocation and approval.
// NOTE: this HttpClient must live for the app's lifetime (skillsProvider/agent use it per request via
// BearerTokenHandler), so it's registered with DI for shutdown-time disposal instead of `using var` -
// a `using var` here disposes it right after startup, cancelling every subsequent MCP request.
string toolboxName = "my-toolbox";

var skillsProvider = await builder.GetTestAgentSkillsProviderAsync(projectClient, toolboxName, foundrySettings, credential);

// Agent itself.
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
        AIContextProviders = [
            memoryProvider,
            skillsProvider
        ],
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
builder.Services.AddFoundryToolboxes(credential);

// This adds OpenAI Conversations endpoints, but I guess with the combination of FoundryResponses and AG-UI,
// Isn't using this? The conversation id comes from the first response.

// TODO: test the theory
builder.Services.AddOpenAIConversations(); // Maybe I don't need this with foundry responses? Perhaps just need to map?

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