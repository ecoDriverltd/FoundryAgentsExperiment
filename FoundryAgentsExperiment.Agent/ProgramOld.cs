//// Copyright (c) Microsoft. All rights reserved.

//using Azure.AI.Projects;
//using Azure.Core;
//using Azure.Identity;
//using Microsoft.Agents.AI;
//using Microsoft.Agents.AI.Foundry.Hosting;
//using Microsoft.Agents.AI.Hosting;
//using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
//using SimpleAgent;

//var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

//var builder = WebApplication.CreateBuilder(args);



//if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
//    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS")))
//{
//    builder.WebHost.UseUrls($"http://+:{port}", $"https://+:{port}");
//}

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

//var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);

//Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
//Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

//TokenCredential credential = new ChainedTokenCredential(
//    new DevTemporaryTokenCredential(),
//    new DefaultAzureCredential(new DefaultAzureCredentialOptions
//    {
//        ExcludeManagedIdentityCredential = string.Equals(
//            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
//            "Development",
//            StringComparison.OrdinalIgnoreCase)
//    }));

//string agentName = "agent-dotnet";
//var projectClient = new AIProjectClient(foundrySettings.ProjectUri, foundrySettings.GetCredential(builder.Environment));

////VectorStore vectorStore = new InMemoryVectorStore();

//AIAgent agent = projectClient
//    .AsAIAgent(
//        model: foundrySettings.DeploymentName,
//        name: agentName,
//        instructions: """
//            You are a helpful AI assistant hosted as a Foundry Hosted Agent.
//            You can help with a wide range of tasks including answering questions,
//            providing explanations, brainstorming ideas, and offering guidance.
//            Be concise, clear, and helpful in your responses.
//            """);

////AIAgent agent2 = projectClient
////    .AsAIAgent(new ChatClientAgentOptions()
////        {
////            Name = agentName,
////            ChatOptions = new() 
////            {
////                ModelId = foundrySettings.DeploymentName,
////                Instructions = """
////                    You are a helpful AI assistant hosted as a Foundry Hosted Agent.
////                    You can help with a wide range of tasks including answering questions,
////                    providing explanations, brainstorming ideas, and offering guidance.
////                    Be concise, clear, and helpful in your responses.
////                    """                
////            },
////            ChatHistoryProvider = new VectorChatHistoryProvider(vectorStore),
////    });

//var sessionStore = agent.GetService<AgentSessionStore>();

//builder.Services.AddFoundryResponses(agent);

////builder.Services.AddOpenAIConversations();

////builder.Services.AddOpenAIResponses();

//if (builder.Environment.IsDevelopment())
//{
//    builder.Services.AddDevTemporaryLocalContributorSetup();
//}

////builder.Services.AddOpenAIResponses();
//// Do I need this locally to mimic what foundry will do when published?
////builder.Services.AddOpenAIConversations();

//builder.Services.AddAGUI();

//var app = builder.Build();

//// Enable CORS
//app.UseCors();

//// Work around hosted storage conflicts caused by replayed platform response IDs.
//app.Use(UseSdkGeneratedResponseIdsForResponses);

////app.UseFoundryLocalUserIdFallback();

//// Map Foundry Responses API endpoint at /responses.
//app.MapFoundryResponses();
//app.MapDevTemporaryLocalAgentEndpoint();

//app.MapGet("/liveness", () => Results.Ok("Healthy"));
////app.MapGet("/readiness", () => Results.Ok("Ready")); // Doesn't MapFoundryResponses do this already?

////app.MapOpenAIResponses();

//// Lets try mapping the conversations endpoint to see if we can get a thread going...
////app.MapOpenAIConversations();

//app.MapAGUI("/ag-ui", agent);

//app.Run();

//const string AgentResponseIdHeader = "x-agent-response-id";

//static async Task UseSdkGeneratedResponseIdsForResponses(HttpContext context, Func<Task> next)
//{
//    if (HttpMethods.IsPost(context.Request.Method)
//        && IsFoundryResponsesPath(context.Request.Path.Value)
//        && context.Request.Headers.ContainsKey(AgentResponseIdHeader))
//    {
//        context.Request.Headers.Remove(AgentResponseIdHeader);
//    }

//    await next();
//}

//static bool IsFoundryResponsesPath(string? path)
//    => string.Equals(path, "/responses", StringComparison.OrdinalIgnoreCase)
//       || (path?.EndsWith("/endpoint/protocols/openai/responses", StringComparison.OrdinalIgnoreCase) ?? false);

//sealed class DevTemporaryTokenCredential : TokenCredential
//{
//    private const string EnvironmentVariable = "AZURE_BEARER_TOKEN";
//    private readonly string? token = Environment.GetEnvironmentVariable(EnvironmentVariable);

//    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
//        => GetAccessToken();

//    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
//        => new(GetAccessToken());

//    private AccessToken GetAccessToken()
//    {
//        if (string.IsNullOrWhiteSpace(token) || string.Equals(token, nameof(DefaultAzureCredential), StringComparison.Ordinal))
//        {
//            throw new CredentialUnavailableException($"{EnvironmentVariable} environment variable is not set.");
//        }

//        return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
//    }
//}


//// Notes section:

//// The MapAGUI extension says this:

///// <summary>
///// Maps an AG-UI agent endpoint.
///// </summary>
///// <param name="endpoints">The endpoint route builder.</param>
///// <param name="pattern">The URL pattern for the endpoint.</param>
///// <param name="aiAgent">The agent instance.</param>
///// <returns>An <see cref="IEndpointConventionBuilder"/> for the mapped endpoint.</returns>
///// <remarks>
///// <para>
///// If an <see cref="AgentSessionStore"/> is registered in dependency injection keyed by the agent's name,
///// it will be used to persist conversation sessions across requests using the AG-UI thread ID as the
///// conversation identifier. If no session store is registered, sessions are ephemeral (not persisted).
///// </para>
///// <para>
///// <strong>Trust model.</strong> The AG-UI <c>RunAgentInput.ThreadId</c> arrives
///// from the wire and is treated as a chain-resume identifier — <em>not</em> as an
///// authorization token. The <see cref="AgentSessionStore"/> contract carries no
///// principal/owner dimension, so when a persistent store is registered any caller
///// who knows or guesses another caller's <c>ThreadId</c> can resume that other
///// caller's persisted thread. Hosts that serve more than one user must compose a
///// principal dimension into the lookup key. The recommended way is to wrap the
///// keyed <see cref="AgentSessionStore"/> in
///// <see cref="IsolationKeyScopedAgentSessionStore"/>, typically by calling
///// <c>UseClaimsBasedSessionIsolation(...)</c> from
///// <c>Microsoft.Agents.AI.Hosting.AspNetCore</c> (or by registering a custom
///// <see cref="SessionIsolationKeyProvider"/>) and registering the store via the
///// <c>WithSessionStore(...)</c> / <c>WithInMemorySessionStore(...)</c> helpers on
///// <see cref="IHostedAgentBuilder"/> so that the wrapper is applied. When no
///// isolation provider is registered, behavior is unchanged — the bare
///// <c>ThreadId</c> is used as the conversation identifier, which is appropriate
///// for first-run / single-user / prototyping scenarios but unsafe for
///// multi-user hosts.
///// </para>
///// </remarks>