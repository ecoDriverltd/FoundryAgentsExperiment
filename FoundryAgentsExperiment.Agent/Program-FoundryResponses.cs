//using Azure.AI.Projects;
//using Microsoft.Agents.AI;
//using Microsoft.Agents.AI.Foundry.Hosting;
//using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
//using Microsoft.Extensions.AI;
//using SimpleAgent;

//var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

//var builder = WebApplication.CreateBuilder(args);

//var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);
////AgentHostBuilder builder = AgentHost.CreateBuilder(args);

//if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
//    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS")))
//{
//    builder.WebHost.UseUrls($"http://+:{port}", $"https://+:{port}");
//}

//// OLTP errors with this and AgentHostBuilder, so commenting out for now. Need to investigate further.
//builder.Logging.SetMinimumLevel(LogLevel.Trace);
//builder.AddServiceDefaults();

//// Configure CORS
//builder.Services.AddCors(options =>
//{
//    options.AddDefaultPolicy(policy =>
//    {
//        policy.AllowAnyOrigin()
//              .AllowAnyMethod()
//              .AllowAnyHeader();
//    });
//});

////builder.AddOpenAIResponses();
////builder.AddOpenAIConversations();

//Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
//Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

//string agentName = "agent-dotnet";
//var projectClient = new AIProjectClient(foundrySettings.ProjectUri, foundrySettings.GetCredential(builder.Environment));
//var openAIClient = projectClient.GetProjectOpenAIClient();

////VectorStore vectorStore = new InMemoryVectorStore(new InMemoryVectorStoreOptions()
////{
////    EmbeddingGenerator = openAIClient
////        .GetEmbeddingClient("embeddingDeploymentName")
////        .AsIEmbeddingGenerator()
////});

//// Will this work with a fixed user/session for testing? As such, every chat should resume as if the same conversation?
//// How will a real thing work, accessing http context for user and session id?

//// I think AG-UI might need this to preserve sessions and recall threads.
//// FileSystemAgentSessionStore
//InMemoryAgentSessionStore sessionStore = new();
//builder.Services.AddKeyedSingleton<AgentSessionStore>(agentName, sessionStore);

////AIAgent agent = openAIClient
////    .GetChatClient("gpt-5.4-nano")  //("chat-model") // What's 'GetChatClient' vs. 'GetResponsesClient'?
////    .AsIChatClient()
////    .AsAIAgent(new ChatClientAgentOptions
////    {
////        ChatOptions = new() { Instructions = "You are a helpful assistant." },
////        Name = agentName

////        // Do I need this or will it use memory for test purposes?
////        //AIContextProviders = [
////        //    new ChatHistoryMemoryProvider(
////        //    vectorStore,
////        //    collectionName: "chathistory",
////        //    vectorDimensions: 3072,
////        //    session => new ChatHistoryMemoryProvider.State(
////        //        // Configure where messages are stored
////        //        storageScope: new() { UserId = "user-123", SessionId = "fixed-session-id-1" },
////        //        // Configure where to search (can be broader than storage scope)
////        //        searchScope: new() { UserId = "user-123" }))]
////    });

//AIAgent agent = projectClient
//    .AsAIAgent(new ChatClientAgentOptions
//    {
//        ChatOptions = new() { Instructions = "You are a helpful assistant.", ModelId = foundrySettings.DeploymentName },
//        Name = agentName,

//        // On devUI I see 'Only ConversationId or ChatHistoryProvider may be used, but not both. The service returned a conversation id indicating server-side chat history management, but the agent has a ChatHistoryProvider configured.'
//        // ChatHistoryProvider = new InMemoryChatHistoryProvider(), // TODO: Maybe I can plug in my own memory provider here, just to see what happens internally...

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

//builder.Services.AddKeyedSingleton(agentName, agent);

//builder.Services.AddFoundryResponses();
//builder.Services.AddFoundryToolboxes(foundrySettings.GetCredential(builder.Environment));
//builder.Services.AddOpenAIConversations();

////.AsAIAgent(
////    model: foundrySettings.DeploymentName,
////    name: agentName,
////    instructions: """
////        You are a helpful AI assistant hosted as a Foundry Hosted Agent.
////        You can help with a wide range of tasks including answering questions,
////        providing explanations, brainstorming ideas, and offering guidance.
////        Be concise, clear, and helpful in your responses.
////        """);

//var agentHost = builder.Build();

//agentHost.MapFoundryResponses("/v1");
//agentHost.MapOpenAIConversations();

//// The above now talks through 'devUI at least. Need to work out which client to use in my own .NET code.

//// Doesn't seem compatible with conversation resume, maybe this can be used for specific calls purely to generate UI if needed?
//agentHost.MapAGUI(agentName, "/ag-ui");

//await agentHost.RunAsync();