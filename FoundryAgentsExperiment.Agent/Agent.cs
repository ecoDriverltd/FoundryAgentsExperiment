using Azure.AI.Projects;
using Azure.Core;
using Azure.Core.Diagnostics;
using FoundryAgentsExperiment.Agent.AgentExtensions;
using FoundryAgentsExperiment.Agent.AgentServices;
using FoundryAgentsExperiment.Agent.Endpoints;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using System.Diagnostics.Tracing;
using System.Text.Json;

var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var builder = WebApplication.CreateBuilder(args);

var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);
var sessionPersistence = builder.Configuration
    .GetSection(SessionPersistenceOptions.SectionName)
    .Get<SessionPersistenceOptions>()
    ?? new SessionPersistenceOptions();

TokenCredential credential = FoundrySettings.GetCredential(builder.Environment);

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS")))
{
    builder.WebHost.UseUrls($"http://+:{port}", $"https://+:{port}");
}

// OLTP errors with this and AgentHostBuilder, so using WebApplicationBuilder instead.
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.AddServiceDefaults();

builder.Services.AddHttpClient().AddLogging();

// Backs the server-managed agent session store. Connection name must match the AppHost Cosmos resource.
var cosmosEndpoint = builder.Configuration.GetConnectionString("cosmos")
    ?? throw new InvalidOperationException("Missing 'cosmos' connection string. Ensure the AppHost references the Cosmos DB resource.");
var cosmosClient = new CosmosClient(cosmosEndpoint, credential);

builder.AddCosmosDbContext<AgentSessionDbContext>("cosmos", "agent-history");

Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

const string agentName = "agent-dotnet";
const string ResponsesAgentSessionIdItem = "responses-agent-session-id";
var projectClient = new AIProjectClient(foundrySettings.ProjectUri, credential);
var openAIClient = projectClient.GetProjectOpenAIClient();
var modelRequestLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
var modelRequestLogger = modelRequestLoggerFactory.CreateLogger("ModelRequestLogging");
using var azureSdkDiagnostics = AzureEventSourceListener.CreateConsoleLogger(EventLevel.Informational);

// Shared instance so the memory provider and session store resolve the same request context.
var httpContextAccessor = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);

builder.Services.AddCosmosAgentSessionStore(agentName);

// Registering this lets the Responses endpoint auto-wrap the AgentSessionStore in an
// IsolationKeyScopedAgentSessionStore, the framework's own idiomatic mechanism for scoping
builder.Services.AddSingleton<AgentIsolationKeyProvider, AgentUserIdIsolationKeyProvider>();

// Foundry-managed memory: FoundryMemoryProvider is an AIContextProvider, so it plugs straight into
// ChatClientAgentOptions.AIContextProviders. Foundry runs the chat/embedding models for extraction and
// search itself - we only need to name the deployments, not wire up an embedding client ourselves.
// Memories are scoped per caller using the same x-agent-user-id header convention as
// FoundryBackedAgentSessionStore (not the unrelated x-memory-user-id header used by the hosted
// memory-search *tool* on versioned Foundry agents).
var memoryProvider = await projectClient.GetFoundryMemoryProviderAsync(agentName, httpContextAccessor, foundrySettings);
var timingMemoryProvider = new TimingAIContextProvider(
    "FoundryMemoryProvider",
    memoryProvider,
    modelRequestLoggerFactory.CreateLogger<TimingAIContextProvider>());

// Create a POC toolbox/skill/mcp skill provider for the agent to use. The Foundry MCP server handles skill invocation and approval.
// NOTE: this HttpClient must live for the app's lifetime (skillsProvider/agent use it per request via
// BearerTokenHandler), so it's registered with DI for shutdown-time disposal instead of `using var` -
// a `using var` here disposes it right after startup, cancelling every subsequent MCP request.
string toolboxName = "my-toolbox";
var skillsProvider = await builder.GetTestAgentSkillsProviderAsync(projectClient, toolboxName, foundrySettings, credential);

// Compaction shapes the current model request while the AgentSessionStore retains durable session state.
// Reusing the same Responses client/model as the summarizer for simplicity in this experiment; swap
// in a smaller/cheaper deployment here if one becomes available.
var summarizerChatClient = new ModelRequestLoggingChatClient(projectClient
    .GetProjectOpenAIClient()
    .GetProjectResponsesClient()
    .AsIChatClientWithStoredOutputDisabled(), modelRequestLogger);

var compactionProvider = new CompactionProvider(
    new SummarizationCompactionStrategy(
        chatClient: summarizerChatClient,
        trigger: CompactionTriggers.TokensExceed(sessionPersistence.CompactionTriggerTokens),
        minimumPreservedGroups: sessionPersistence.CompactionMinimumPreservedGroups));

var cosmosChatHistoryProvider = new CosmosChatHistoryProvider(
    cosmosClient,
    databaseId: "agent-history",
    containerId: "agent-chat-history",
    stateInitializer: session =>
    {
        var agentSessionId = httpContextAccessor.HttpContext?.Items[ResponsesAgentSessionIdItem] as string;
        if (!string.IsNullOrWhiteSpace(agentSessionId))
        {
            session?.StateBag.SetValue(CosmosAgentSessionStore.ConversationIdStateBagKey, agentSessionId);
            return new CosmosChatHistoryProvider.State(agentSessionId);
        }

        if (session?.StateBag.TryGetValue<string>(CosmosAgentSessionStore.ConversationIdStateBagKey, out var threadId) == true &&
            !string.IsNullOrWhiteSpace(threadId))
        {
            return new CosmosChatHistoryProvider.State(threadId);
        }

        throw new InvalidOperationException("The Responses conversation ID must be available in the agent session before chat history can be persisted.");
    });

var chatHistoryProvider = new LoggingChatHistoryProvider(
    new DeduplicatingCosmosChatHistoryProvider(
        cosmosChatHistoryProvider,
        modelRequestLoggerFactory.CreateLogger<DeduplicatingCosmosChatHistoryProvider>()),
    modelRequestLoggerFactory.CreateLogger<LoggingChatHistoryProvider>());

var modelChatClient = projectClient
    .GetProjectOpenAIClient()
    .GetProjectResponsesClient()
    .AsIChatClientWithStoredOutputDisabled();

AIAgent agent = modelChatClient
    .AsBuilder()
    // Important that these providers are at this layer. Compaction and memory change what is sent to the LLM, but shouldn't persist in durable storage.
    .UseAIContextProviders([timingMemoryProvider, compactionProvider])
    .BuildAIAgent(new ChatClientAgentOptions
    {
        ChatOptions = new()
        {
            Instructions = """
                You are providing general assistance to a user planning their day.
                If the user asks for their location or where they are or similar, call the get_user_location tool to retrieve it.
                """,
            ModelId = foundrySettings.DeploymentName,
            Tools = [
                AIFunctionFactory.Create(() => DateTimeOffset.UtcNow,
                    name: "get_current_date_time",
                    description: "Get the current UTC date and time."
            )]
        },
        Name = agentName,
        RequirePerServiceCallChatHistoryPersistence = true, // Defaults to true as compaction plus tool calls seems to result in dropped history otherwise.
        ChatHistoryProvider = chatHistoryProvider,
        AIContextProviders = [skillsProvider]
    });

builder.AddAIAgent(agentName, (_, _) => agent)
       .WithSessionStore((sp, agentName) => sp.GetKeyedService<CosmosAgentSessionStore>(agentName)!);

builder.Services.AddFoundryResponses(agent);
builder.Services.AddFoundryToolboxes(credential);

// Needed for conversations in the DevUI site.
builder.Services.AddOpenAIConversations();

var agentHost = builder.Build();

agentHost.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/v1/responses"))
    {
        context.Request.EnableBuffering();
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        context.Request.Body.Position = 0;
        if (document.RootElement.TryGetProperty("agent_session_id", out var agentSessionId) &&
            !string.IsNullOrWhiteSpace(agentSessionId.GetString()))
        {
            context.Items[ResponsesAgentSessionIdItem] = agentSessionId.GetString()!;
        }
    }

    await next(context);
});

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

agentHost.MapConversationEndpoints(agentName);

await agentHost.RunAsync();