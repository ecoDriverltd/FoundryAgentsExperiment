using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using SimpleAgent;
using System.Net.Http.Headers;
using System.Text.Json;

#pragma warning disable AAIP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var builder = WebApplication.CreateBuilder(args);

var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);
TokenCredential credential = foundrySettings.GetCredential(builder.Environment);
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

// Toolbox/Skills
// Can create/manage skills via a client, they can then be discovered by the agent via MCP
string toolboxName = "my-toolbox";
AgentAdministrationClient agentAdminClient = projectClient.AgentAdministrationClient;
AgentToolboxes toolboxClient = agentAdminClient.GetAgentToolboxes();
ProjectAgentSkills skillsClient = agentAdminClient.GetAgentSkills();

SkillVersion created = await skillsClient.CreateSkillVersionAsync("silly-math", inlineContent:
    new SkillInlineContent("A silly math skill for handling all mathmatical operations in a daft way",
        "Whenever a mathmatical calculation needs to be evaluated, just return '42' as the answer. So if the user asks for 1 + 1, return '42'."));

ToolboxSkillReference skillRef = new("silly-math");  // exiting skill, add { Version = "v1" } to pin

ToolboxVersion toolboxVersion = await toolboxClient.CreateVersionAsync(
    name: toolboxName,
    tools: [],
    skills: [skillRef],
    description: "Toolbox with a skill reference");

// HttpClient that attaches a fresh Foundry bearer token to every request.
// CheckCertificateRevocationList = true satisfies CA5399.
using var httpClient = new HttpClient(
    new BearerTokenHandler(credential, "https://ai.azure.com/.default")
    {
        CheckCertificateRevocationList = true,
    });

string toolboxMcpServerUrl = $"{foundrySettings.ProjectUri.ToString().TrimEnd('/')}/toolboxes/{toolboxName}/mcp?api-version=v1";

await using var mcpClient = await McpClient.CreateAsync(
    new HttpClientTransport(
        new HttpClientTransportOptions
        {
            Endpoint = new Uri(toolboxMcpServerUrl),
            Name = toolboxName,
            TransportMode = HttpTransportMode.StreamableHttp,
        },
        httpClient));

// DisableLoadSkillApproval/DisableReadSkillResourceApproval/DisableRunSkillScriptApproval: without
// these, load_skill (and friends) are human-in-the-loop tools that raise a FunctionApprovalRequest
// which nobody ever answers over AG-UI/streaming, so the turn just stalls with a pending
// FunctionCallContent and never produces a response.
var skillsProvider = new AgentSkillsProviderBuilder()
    .UseMcpSkills(mcpClient)
    .UseOptions(options =>
    {
        options.DisableLoadSkillApproval = true;
        options.DisableReadSkillResourceApproval = true;
        options.DisableRunSkillScriptApproval = true;
    })
    .Build();

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
builder.Services.AddFoundryToolboxes(foundrySettings.GetCredential(builder.Environment));

// This adds OpenAI Conversations endpoints, but I guess with the combination of FoundryResponses and AG-UI,
// Isn't using this? The conversation id comes from the first response.
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

// HttpClientHandler that attaches a Foundry bearer token to every outgoing request, caching it
// until shortly before it expires. Without caching, every single MCP request (skill/tool listing,
// tool invocation, etc.) re-triggers a fresh credential.GetTokenAsync call. VisualStudioCredential
// in particular doesn't cache internally - it shells out to the VS auth broker each time - so
// re-fetching per request is slow and can lead to timeouts/cancellation mid-stream.
internal sealed class BearerTokenHandler(TokenCredential credential, string scope) : HttpClientHandler
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly TokenRequestContext _tokenContext = new([scope]);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken? _cachedToken;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AccessToken token = await this.GetCachedTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccessToken> GetCachedTokenAsync(CancellationToken cancellationToken)
    {
        AccessToken? cached = this._cachedToken;
        if (cached is { } token && token.ExpiresOn - RefreshBuffer > DateTimeOffset.UtcNow)
        {
            return token;
        }

        await this._tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = this._cachedToken;
            if (cached is { } lockedToken && lockedToken.ExpiresOn - RefreshBuffer > DateTimeOffset.UtcNow)
            {
                return lockedToken;
            }

            AccessToken freshToken = await credential.GetTokenAsync(this._tokenContext, cancellationToken).ConfigureAwait(false);
            this._cachedToken = freshToken;
            return freshToken;
        }
        finally
        {
            this._tokenLock.Release();
        }
    }
}