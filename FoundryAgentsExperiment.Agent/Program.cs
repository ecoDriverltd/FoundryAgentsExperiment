using Azure.AI.Projects;
using Microsoft.Agents.AI;
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

//VectorStore vectorStore = new InMemoryVectorStore(new InMemoryVectorStoreOptions()
//{
//    EmbeddingGenerator = openAIClient
//        .GetEmbeddingClient("embeddingDeploymentName")
//        .AsIEmbeddingGenerator()
//});

// Will this work with a fixed user/session for testing? As such, every chat should resume as if the same conversation?
// How will a real thing work, accessing http context for user and session id?

// NOTE: Microsoft.Agents.AI.Foundry.Hosting.InMemoryAgentSessionStore implements a DIFFERENT
// AgentSessionStore contract (Foundry-specific, used by MapFoundryResponses/MapOpenAIConversations)
// than the one MapAGUI resolves (Microsoft.Agents.AI.Hosting.AgentSessionStore). Registering that
// type here was silently ignored by MapAGUI - use the AG-UI-compatible store instead.
builder.Services.AddFoundryBackedAgentSessionStore(agentName);

//var test2 = new FileSystemAgentSessionStore()

//AIAgent agent = openAIClient
//    .GetChatClient("gpt-5.4-nano")  //("chat-model") // What's 'GetChatClient' vs. 'GetResponsesClient'?
//    .AsIChatClient()
//    .AsAIAgent(new ChatClientAgentOptions
//    {
//        ChatOptions = new() { Instructions = "You are a helpful assistant." },
//        Name = agentName

//        // Do I need this or will it use memory for test purposes?
//        //AIContextProviders = [
//        //    new ChatHistoryMemoryProvider(
//        //    vectorStore,
//        //    collectionName: "chathistory",
//        //    vectorDimensions: 3072,
//        //    session => new ChatHistoryMemoryProvider.State(
//        //        // Configure where messages are stored
//        //        storageScope: new() { UserId = "user-123", SessionId = "fixed-session-id-1" },
//        //        // Configure where to search (can be broader than storage scope)
//        //        searchScope: new() { UserId = "user-123" }))]
//    });

AIAgent agent = projectClient
    .AsAIAgent(new ChatClientAgentOptions
    {
        ChatOptions = new() { Instructions = "You are a helpful assistant.", ModelId = foundrySettings.DeploymentName },
        Name = agentName,

        // Do I need this or will it use memory for test purposes?
        //AIContextProviders = [
        //    new ChatHistoryMemoryProvider(
        //    vectorStore,
        //    collectionName: "chathistory",
        //    vectorDimensions: 3072,
        //    session => new ChatHistoryMemoryProvider.State(
        //        // Configure where messages are stored
        //        storageScope: new() { UserId = "user-123", SessionId = "fixed-session-id-1" },
        //        // Configure where to search (can be broader than storage scope)
        //        searchScope: new() { UserId = "user-123" }))]
    });

// NOTE: agent is shared by both /v1 (AddFoundryResponses) and /ag-ui (MapAGUI via this keyed
// singleton). Do NOT set ChatHistoryProvider on it. Foundry's projectClient returns a
// service-managed ConversationId on every response, and ChatClientAgent.UpdateSessionConversationId
// throws InvalidOperationException ("Only ConversationId or ChatHistoryProvider may be used, but
// not both") if a ChatHistoryProvider is also explicitly configured. That exception previously
// aborted every AG-UI streaming response mid-turn, which meant AgentSessionStore.SaveSessionAsync
// was never reached - the session store stayed empty and each turn silently started a fresh session.
builder.Services.AddKeyedSingleton(agentName, agent);

builder.Services.AddFoundryResponses(agent);
builder.Services.AddFoundryToolboxes(foundrySettings.GetCredential(builder.Environment));

// This says for dev/test doing in memory, so I guess you need to register something more permanent for production.
builder.Services.AddOpenAIConversations();

//.AsAIAgent(
//    model: foundrySettings.DeploymentName,
//    name: agentName,
//    instructions: """
//        You are a helpful AI assistant hosted as a Foundry Hosted Agent.
//        You can help with a wide range of tasks including answering questions,
//        providing explanations, brainstorming ideas, and offering guidance.
//        Be concise, clear, and helpful in your responses.
//        """);

var agentHost = builder.Build();

agentHost.MapFoundryResponses("/v1");
agentHost.MapOpenAIConversations();

// The above now talks through 'devUI at least. Need to work out which client to use in my own .NET code.

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

        var log = context.RequestServices.GetRequiredService<ILogger<Program>>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var threadId = doc.RootElement.TryGetProperty("threadId", out var t) ? t.GetString() : "(none)";
            var messageCount = doc.RootElement.TryGetProperty("messages", out var m) ? m.GetArrayLength() : -1;
            log.LogInformation("[Wire] POST /ag-ui threadId={ThreadId} messageCount={MessageCount}", threadId, messageCount);
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
agentHost.MapAGUI(agentName, "/ag-ui");

await agentHost.RunAsync();