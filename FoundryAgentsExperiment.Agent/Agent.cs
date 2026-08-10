using Azure.AI.Projects;
using Azure.Core;
using FoundryAgentsExperiment.Agent;
using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
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

// Backs the client-managed chat history (CosmosChatHistoryProvider). Connection name must match
// the AppHost's AddAzureCosmosDB("cosmos"). Constructed directly because the Agent Framework
// provider requires a CosmosClient.
var cosmosEndpoint = builder.Configuration.GetConnectionString("cosmos")
    ?? throw new InvalidOperationException("Missing 'cosmos' connection string. Ensure the AppHost references the Cosmos DB resource.");
var cosmosClient = new CosmosClient(cosmosEndpoint, credential);

builder.AddCosmosDbContext<ConversationIndexDbContext>("cosmos", "agent-history");

Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

const string agentName = "agent-dotnet";
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

// Mode 2 (CosmosChatHistoryProvider) owns the durable transcript, so the AG-UI session store no
// longer needs to persist or look up anything - it just tags new sessions with the wire threadId
// (see ThinAgentSessionStore) and, on save, updates the lightweight conversation index.
builder.Services.AddThinAgentSessionStore(agentName);

// Registering this lets MapAGUIServer auto-wrap the AgentSessionStore in an
// IsolationKeyScopedAgentSessionStore, the framework's own idiomatic mechanism for scoping
builder.Services.AddSingleton<Microsoft.Agents.AI.Hosting.SessionIsolationKeyProvider, AgentUserIdSessionIsolationKeyProvider>();

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
// Mode 2 ("client-managed with store=false"): the Responses client is wrapped with
// AsIChatClientWithStoredOutputDisabled(), so Foundry never returns a service-managed
// ConversationId. This lets us pair it with our own CosmosChatHistoryProvider without hitting
// ChatClientAgent's "Only ConversationId or ChatHistoryProvider may be used, but not both" guard
// (see historical note this replaces, previously below this block).
// https://devblogs.microsoft.com/agent-framework/chat-history-storage-patterns-in-microsoft-agent-framework/#fixed-mode-providers
var chatHistoryProvider = new CosmosChatHistoryProvider(
    cosmosClient,
    databaseId: "agent-history",
    containerId: "chat-history",
    stateInitializer: session => new CosmosChatHistoryProvider.State(
        conversationId: session?.StateBag.TryGetValue<string>(ThinAgentSessionStore.ConversationIdStateBagKey, out var threadId) == true ? threadId ?? Guid.NewGuid().ToString("n") : Guid.NewGuid().ToString("n"),
        tenantId: "dev",
        userId: httpContextAccessor.GetAgentUserId()))
{
    // Default is 24 hours, which is too short for resumable conversations - keep messages around
    // for about a year before Cosmos's background TTL sweep reclaims them. Requires TTL to be
    // enabled on the "chat-history" container (DefaultTimeToLive set, e.g. to -1) for this to take effect.
    MessageTtlSeconds = (int)TimeSpan.FromDays(365).TotalSeconds,
};

builder.Services.AddSingleton<CosmosConversationIndexStore>();

// Compaction (Option B): registered at the IChatClient level via UseAIContextProviders, so it sits
// BENEATH ChatHistoryProvider's load/store hooks entirely. ChatHistoryProvider still only ever sees
// and persists the original, untouched messages - the synthetic summary message produced here is
// purely an in-memory shaping of what gets sent to the model on this turn, and never reaches Cosmos.
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
            Instructions = "You are a helpful assistant.",
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

// Warm up the Cosmos client/credential before serving any requests. CosmosClient acquires its
// AAD token lazily on the first real network call, and locally that's VisualStudioCredential (see
// FoundrySettings.GetCredential), which can take several seconds to resolve a cached token. Without
// this, that latency lands on the first live turn's SaveSessionAsync/CosmosChatHistoryProvider call
// instead of here at startup.
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

// Debug-only logging of the raw wire payload for every /ag-ui call BEFORE MapAGUI's handler runs.
// The conversation index is now updated from ThinAgentSessionStore.SaveSessionAsync (which
// MapAGUIServer guarantees fires exactly once per turn, after streaming completes), not from here.
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
            var threadId = doc.RootElement.TryGetProperty("threadId", out var t) ? t.GetString() : null;
            var messageCount = doc.RootElement.TryGetProperty("messages", out var m) ? m.GetArrayLength() : -1;
            log.LogInformation("[Wire] POST /ag-ui threadId={ThreadId} messageCount={MessageCount} userId={UserId}", threadId, messageCount, userIdHeader);
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
// ThinAgentSessionStore adapts our own conversation index/ChatHistoryProvider setup to the AG-UI-compatible contract.
agentHost.MapAGUIServer(agentName, "/ag-ui");

// Need a way of getting a conversation history back for a given user (and ideally get historical conversations for a user as well).
agentHost.MapGet("/get-chat-conversation/{conversationId}",
    async (Microsoft.Agents.AI.Hosting.AgentSessionStore sessionStore,
    [FromRoute] string conversationId,
    [FromKeyedServices(agentName)] AIAgent agent,
    CancellationToken ct = default) =>
    {
        // If I use my 'FoundryBackedAgentSessionStore', it's going to presumably get from the file system, which is transient/destroyed on restart.
        // Do I end up needing my own persistent storage to return chat history for resume? Seems to defeat some of the value of foundry managed chat history to an extent.
        // Maybe I do simply plug in my own, use cosmosDb instead of the local file storage?

        var session = await sessionStore.GetSessionAsync(agent, conversationId, ct);
        return session;
    });

// Lists a user's past conversations (id, title, timestamps) from the lightweight Cosmos index,
// without needing to load each conversation's full chat history.
agentHost.MapGet("/conversations",
    async (CosmosConversationIndexStore conversationIndexStore,
    IHttpContextAccessor httpContextAccessor,
    CancellationToken ct = default) =>
    {
        var userId = httpContextAccessor.GetAgentUserId();
        var conversations = await conversationIndexStore.ListConversationsAsync(userId, ct);
        return conversations;
    });

await agentHost.RunAsync();