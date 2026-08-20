using Azure.AI.Projects;
using Azure.Core;
using FoundryAgentsExperiment.Agent.AgentExtensions;
using FoundryAgentsExperiment.Agent.AgentServices;
using FoundryAgentsExperiment.Agent.Endpoints;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

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

// Backs the server-managed chat history. Connection name must match the AppHost Cosmos resource.
var cosmosEndpoint = builder.Configuration.GetConnectionString("cosmos")
    ?? throw new InvalidOperationException("Missing 'cosmos' connection string. Ensure the AppHost references the Cosmos DB resource.");
var cosmosClient = new CosmosClient(cosmosEndpoint, credential);

builder.AddCosmosDbContext<ConversationIndexDbContext>("cosmos", "agent-history");
builder.AddCosmosDbContext<AgentSessionDbContext>("cosmos", "agent-history");

Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

const string agentName = "agent-dotnet";
var projectClient = new AIProjectClient(foundrySettings.ProjectUri, credential);
var openAIClient = projectClient.GetProjectOpenAIClient();

// Shared instance so the memory provider and session store resolve the same request context.
var httpContextAccessor = new HttpContextAccessor();
var messagePersistenceTracker = new ChatMessagePersistenceTracker();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
builder.Services.AddSingleton(messagePersistenceTracker);

builder.Services.AddCosmosAgentSessionStore(agentName);

// Registering this lets MapAGUIServer auto-wrap the AgentSessionStore in an
// IsolationKeyScopedAgentSessionStore, the framework's own idiomatic mechanism for scoping
builder.Services.AddSingleton<AgentIsolationKeyProvider, AgentUserIdIsolationKeyProvider>();

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

var chatHistoryProvider = CosmosChatHistoryProviderFactory.Create(
    cosmosClient,
    httpContextAccessor,
    messagePersistenceTracker);

builder.Services.AddSingleton<CosmosConversationIndexStore>();

// Compaction runs beneath ChatHistoryProvider's load/store hooks, preserving the original transcript
// while using an in-memory summary to shape the current model request.
// Reusing the same Responses client/model as the summarizer for simplicity in this experiment; swap
// in a smaller/cheaper deployment here if one becomes available.
var summarizerChatClient = projectClient
    .GetProjectOpenAIClient()
    .GetProjectResponsesClient()
    .AsIChatClientWithStoredOutputDisabled();

var compactionProvider = new CompactionProvider(
    new SummarizationCompactionStrategy(
        chatClient: summarizerChatClient,
        trigger: CompactionTriggers.TokensExceed(50_000)));

AIAgent agent = projectClient
    .GetProjectOpenAIClient()
    .GetProjectResponsesClient()
    .AsIChatClientWithStoredOutputDisabled()
    .AsBuilder()
    .UseAIContextProviders(compactionProvider)
    .BuildAIAgent(new ChatClientAgentOptions
    {
        ChatOptions = new()
        {
            Instructions = """
                You are providing general assistance to a user planning their day.
                If the user asks for their location or where they are or similar, call the get_user_location tool to retrieve it.
                """,
            ModelId = foundrySettings.DeploymentName,
            //Tools = []
        },
        Name = agentName,
        ChatHistoryProvider = chatHistoryProvider,
        AIContextProviders = [
            memoryProvider,
            skillsProvider
        ],
    });

builder.Services.AddKeyedSingleton(agentName, agent);

builder.Services.AddFoundryResponses(agent);
builder.Services.AddFoundryToolboxes(credential);

// This adds OpenAI Conversations endpoints, but I guess with the combination of FoundryResponses and AG-UI,
// Isn't using this? The conversation id comes from the first response.

// Needed for conversations in the DevUI site, but presumable not 
builder.Services.AddOpenAIConversations();

var agentHost = builder.Build();

try
{
    await cosmosClient.ReadAccountAsync();
}
catch (Exception ex)
{
    agentHost.Logger.LogWarning(ex, "Cosmos client warmup failed; the first request may pay this latency instead.");
}

// These two seem to let things work in DevUI
agentHost.MapFoundryResponses("/v1");
agentHost.MapOpenAIConversations();

// Optional diagnostics only; this middleware does not affect request processing or persistence.
agentHost.UseAGUIRequestLogging();

// The AG-UI server persists session state by protocol thread ID while CosmosChatHistoryProvider
// retains the server-managed transcript.
agentHost.MapAGUIServer(agentName, "/ag-ui");

agentHost.MapConversationEndpoints(agentName, chatHistoryProvider);

await agentHost.RunAsync();