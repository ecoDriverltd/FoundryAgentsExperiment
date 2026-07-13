using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using Xunit;

namespace FoundryAgentsExperiment.IntegrationTests;

/// <summary>
/// Integration tests exercising the AG-UI + Foundry hosted agent via the
/// AIAgent / AgentSession pattern from the Agent Framework docs.
///
/// Architecture confirmed:
///   - AgentSession carries the thread ID; Foundry stores conversation server-side.
///   - Each turn sends ONLY [system + current user message] — no client-side history.
///   - A unique user ID per test isolates Foundry threads between runs.
/// </summary>
[Trait("Category", "Integration")]
public class AgentChatTests : IAsyncLifetime
{
    private DistributedApplication? app;
    private readonly ITestOutputHelper _output;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public AgentChatTests(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.FoundryAgentsExperiment_AppHost>(
                args: [],
                configureBuilder: (appOptions, hostSettings) =>
                {
                    appOptions.DisableDashboard = false;
                },
                cancellationToken: cancellationToken);

        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);

            logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire", LogLevel.Debug);
        });

        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        app = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("agent-dotnet", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
            await app.DisposeAsync();
    }

    // Agent talking through AG-UI interface on foundry agent. Not clear if this can use conversations via openAI?
    private ChatClientAgent CreateAGUIAgent(string userId)
    {
        var http = app!.CreateHttpClient("agent-dotnet");
        http.DefaultRequestHeaders.Add("x-agent-user-id", userId);
        return new AGUIChatClient(http, "/ag-ui")
            .AsAIAgent(name: "agui-client", description: "AG-UI Client Agent");
    }

    // Perhaps we need to use the openAI agent for the conversation history stuff to work locally? 
    //private async Task CreateOpenAIAgent(string userId)
    //{
    //    var projectClient = await GetAIProjectClient();

    //    var something = projectClient.GetProjectOpenAIClient()
    //        .GetProjectResponsesClient()
    //        .AsIChatClient()
    //}

    //private async Task<ProjectResponsesClient> CreateAgentFromProject()
    //{
    //    var projectClient = await GetAIProjectClient();


    //    projectClient.GetProjectOpenAIClient()
    //        .GetResponsesClient()

    //    var responsesClient = projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgentEndpoint("agent-dotnet");



    //    return responsesClient.asAi;
    //}

    // Creates a 'FoundryAgent' as the ChatClient from this should support managed conversation history.
    // However, I'm unclear if my client should be trying to talk to the local agent that I can see in aspire,
    // or if that's a proxy for the foundry hosted one, or if the there is anything foundry hosted when this is debugging locally.
    // The comment for 'RunAsHostedAgent' says: "Configures the resource to run and publish as a hosted agent in Microsoft Foundry"
    // However I get a local resource...so not sure I understand. Following an online code sample,
    // there's lots of code to make the local thing run as if it's foundry, so I'll try a Foundry client pointed at the local thing
    // this that code copied...
    private async Task<FoundryAgent> CreateFoundryAgentAsync()
    {
        //var http = app!.CreateHttpClient("agent-dotnet");
        //http.DefaultRequestHeaders.Add("x-agent-user-id", userId);

        var credential = new ChainedTokenCredential(
            //new DevTemporaryTokenCredential(),
            new VisualStudioCredential(),
            new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeManagedIdentityCredential = string.Equals(
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                    "Development",
                    StringComparison.OrdinalIgnoreCase)
            }));

        // Need a url to the project, then the project client
        string foundryProjectUrl = await app!.GetConnectionStringAsync("agent-test") ?? throw new ArgumentException("No connection string for 'agent-test' found.");
        var connectionBuilder = new DbConnectionStringBuilder { ConnectionString = foundryProjectUrl };
        var projectEndpoint = connectionBuilder["Endpoint"]?.ToString() ?? throw new ArgumentException("No 'Endpoint' in connection string.");
        var agentUrl = app!.GetEndpoint("agent-dotnet", "https");

        // Project endpoint is like: https://<azureFoundryUrl>/api/projects/agent-test
        //AIProjectClient aiProjectClient = new(new Uri(projectEndpoint), credential);

        // Does the project client need to go local?
        var localMagicUri = new Uri(agentUrl, "/api/projects/agent-test");
        AIProjectClient aiProjectClient = new(localMagicUri, credential);

        // This doesn't exist as we're not dealing with published running locally, the 'hosted' agent
        // is running on local host until published. So what is the local development inner loop if 
        // you want a foundry hosted agent with foundry conversation history?
        //AgentReference agentRef = new("agent-dotnet");
        //var agent = aiProjectClient.AsAIAgent(agentRef);

        // Get this with the local agent url, so do I need to fake it to make it?
        // I think the agent code has some middleware for this: 'MapDevTemporaryLocalAgentEndpoint'
        // System.ArgumentException: 'Expected an agent endpoint of shape
        // 'https://<host>/.../projects/<project>/agents/<agentName>/endpoint/protocols/openai'
        // but got 'https://localhost:56779/'. If you want to construct a FoundryAgent against a project endpoint,
        // use the (Uri projectEndpoint, AuthenticationTokenProvider credential, string model, string instructions, ...) constructor instead.
        // (Parameter 'agentEndpoint')'

        var magicalUrlPleaseWork = new Uri(agentUrl, "api/projects/agent-test/agents/agent-dotnet/endpoint/protocols/openai");
        var agent = aiProjectClient.AsAIAgent(magicalUrlPleaseWork);

        return agent;
    }

    /// <summary>
    /// Use the foundry agent to create a session with a conversation id.
    /// </summary>
    [Fact]
    public async Task FoundryAgentRecallsFact_ConversationSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await CreateFoundryAgentAsync();

        var foundryProjectUrl = await app!.GetConnectionStringAsync("agent-test", cancellationToken: ct);
        _output.WriteLine($"Connection string: {foundryProjectUrl}");

        string answerText = string.Empty;

        //ChatClientAgentSession conversationSession = await agent.CreateConversationSessionAsync(ct);
        //var conversationId = conversationSession.ConversationId;
        //var conversationResponse = await agent.RunAsync("My favourite colour is BLUE42.", conversationSession, cancellationToken: ct);
        //var conversationAnswer = await agent.RunAsync("What is my favourite colour?", conversationSession, cancellationToken: ct);
        //var conversationSessionJson = (await agent.SerializeSessionAsync(conversationSession, cancellationToken: ct)).ToString();
        //answerText = conversationAnswer.Text;

        // Ok, so I can't get a working conversation session out of the agent above, but maybe a regular session will get me one?
        var session = await agent.CreateSessionAsync(cancellationToken: ct);

        // Agent 'agent-dotnet' not found [Request ID: 599b8b6aea8358bedd2c6bf9622e5c37] :(
        var response = await agent.RunAsync("My favourite colour is BLUE42.", session, cancellationToken: ct);
        var answer = await agent.RunAsync("What is my favourite colour?", session, cancellationToken: ct);
        var sessionJson = (await agent.SerializeSessionAsync(session, cancellationToken: ct)).ToString();
        answerText = answer.Text;

        // One of the conversations worked...
        Assert.Contains("BLUE42", answerText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGUIAgentRecallsFact()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = CreateAGUIAgent("test-" + Guid.NewGuid().ToString("N"));
        var conversationClient = await GetConversationClient();

        // I can create a conversation id through foundry, but in this case we're talking through 
        // ag-ui to the agent running locally. Still a bit confused by this,
        // 'AsHostedAgent' says: 'Configures the resource to run and publish as a hosted agent in Microsoft Foundry'
        // So why do we end up with a local url? Does that mean locally it manages the conversation history 
        // and when published it will be managed by foundry?
        ProjectConversation conversation = (await conversationClient.CreateProjectConversationAsync(cancellationToken: ct)).Value;

        // From the comments in 'CreateSessionAsync':
        //     Agent threads created with this method will only work with Microsoft.Agents.AI.ChatClientAgent
        //     instances that support server-side conversation storage through their underlying
        //     Microsoft.Extensions.AI.IChatClient.

        AgentSession session = await agent.CreateSessionAsync(conversation.Id, ct);

        // Give agent some information.
        var response1 = await agent.RunAsync("My favourite colour is BLUE42.", session, cancellationToken: ct);
        var responseId = response1.AsChatResponse().ResponseId;

        var historyProvider = agent.ChatHistoryProvider; // This comes up as the inMemoryProvider, which doesn't bode well for the ag-ui stuff working
                                                         // with a foundry hosted agent and conversation history.
        var chatClient = agent.ChatClient;
        string chatClientType = chatClient.GetType().FullName ?? "unknown";

        var sessionSerialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        var sessionText = sessionSerialized.ToString();

        // Ask it to recall information in new session but with same conversation id.
        AgentSession session2 = await agent.CreateSessionAsync(conversation.Id, ct);

        var result = await agent.RunAsync("What is my favourite colour?", session2, cancellationToken: ct);
        var textWithConversationId = result.Text;

        if (!result.Text.Contains("BLUE42", StringComparison.OrdinalIgnoreCase))
        {
            Assert.NotNull(responseId);

            // Ok, what if the response id can be used as a kind of thread identifier to continue the chat?
            AgentSession session3 = await agent.CreateSessionAsync(responseId, ct);
            var result2 = await agent.RunAsync("What is my favourite colour?", session3, cancellationToken: ct);
            var textWithResponseId = result2.Text;

            // If this works, we can use the responseId to continue a thread.
            Assert.Contains("BLUE42", textWithResponseId, StringComparison.OrdinalIgnoreCase);

            // But it doesn't...
        }
        else
        {
            // This doesn't work, so the agent can't use the conversation id from the foundry project at least against the local AG-UI endpoint.
            Assert.Contains("BLUE42", textWithConversationId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<AIProjectClient> GetAIProjectClient()
    {
        var credential = new ChainedTokenCredential(
            new VisualStudioCredential(),
            new VisualStudioCredential());

        // Need a url to the project, then the project client
        string foundryProjectUrl = await app!.GetConnectionStringAsync("agent-test") ?? throw new ArgumentException("No connection string for 'agent-test' found.");
        var connectionBuilder = new DbConnectionStringBuilder { ConnectionString = foundryProjectUrl };
        var projectEndpoint = connectionBuilder["Endpoint"]?.ToString() ?? throw new ArgumentException("No 'Endpoint' in connection string.");

        AIProjectClient aiProjectClient = new(new Uri(projectEndpoint), credential);

        return aiProjectClient;
    }

    private async Task<ProjectConversationsClient> GetConversationClient()
    {
        var aiProjectClient = await GetAIProjectClient();
        ProjectConversationsClient conversationClient = aiProjectClient.GetProjectOpenAIClient().GetProjectConversationsClient();

        return conversationClient;
    }

    private IChatClient CreateChatClient(string userId)
    {
        var http = app!.CreateHttpClient("agent-dotnet");
        http.DefaultRequestHeaders.Add("x-agent-user-id", userId);
        return new AGUIChatClient(http, "/ag-ui");
    }

    private static async Task<(string text, string? threadId)> StreamViaChatClientAsync(
        IChatClient client, string? conversationId, IEnumerable<ChatMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        string? returnedThreadId = null;
        var options = new ChatOptions { ConversationId = conversationId };

        await foreach (var update in client.GetStreamingResponseAsync(messages.ToList(), options))
        {
            if (!string.IsNullOrEmpty(update.ConversationId))
                returnedThreadId = update.ConversationId;
            if (!string.IsNullOrEmpty(update.Text))
                sb.Append(update.Text);
        }

        return (sb.ToString(), returnedThreadId);
    }

    /// <summary>
    /// Mirrors exactly what AgentChatService does: IChatClient.GetStreamingResponseAsync
    /// with ConversationId in ChatOptions. Proves within-session continuity on the
    /// IChatClient path (no AIAgent/AgentSession wrapper).
    /// </summary>
    [Fact]
    public async Task IChatClient_TwoTurns_SameClient_AgentRecallsFact()
    {
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var client = CreateChatClient(userId);

        var messages1 = new[]
        {
            new ChatMessage(ChatRole.System, "You are a helpful assistant. Be concise."),
            new ChatMessage(ChatRole.User, "My secret word is CRIMSON9.")
        };

        var (turn1Text, threadId) = await StreamViaChatClientAsync(client, null, messages1);
        _output.WriteLine($"Turn 1: '{turn1Text}'  threadId={threadId}");
        Assert.False(string.IsNullOrWhiteSpace(threadId), "No thread ID returned from turn 1.");

        var messages2 = new[]
        {
            new ChatMessage(ChatRole.System, "You are a helpful assistant. Be concise."),
            new ChatMessage(ChatRole.User, "What secret word did I just tell you?")
        };

        var (turn2Text, _) = await StreamViaChatClientAsync(client, threadId, messages2);
        _output.WriteLine($"Turn 2: '{turn2Text}'");

        Assert.False(string.IsNullOrWhiteSpace(turn2Text), "Turn 2 produced no response.");
        Assert.Contains("CRIMSON9", turn2Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The resume scenario: turn 1 on one client instance, store the thread ID,
    /// create a brand new client (simulating a page refresh or resumed conversation),
    /// turn 2 with the stored thread ID. Proves whether the agent host preserves
    /// session state across disconnected clients — exactly what ChatPage resume does.
    /// </summary>
    [Fact]
    public async Task IChatClient_Resume_NewClient_StoredThreadId_AgentRecallsFact()
    {
        var userId = "test-" + Guid.NewGuid().ToString("N");

        // Turn 1 — first client instance
        var client1 = CreateChatClient(userId);
        var (turn1Text, threadId) = await StreamViaChatClientAsync(client1, null,
        [
            new ChatMessage(ChatRole.System, "You are a helpful assistant. Be concise."),
            new ChatMessage(ChatRole.User, "My secret word is INDIGO5.")
        ]);
        _output.WriteLine($"Turn 1: '{turn1Text}'  threadId={threadId}");
        Assert.False(string.IsNullOrWhiteSpace(threadId), "No thread ID returned from turn 1.");

        // Simulate page refresh: brand new client, only the stored thread ID is available
        var client2 = CreateChatClient(userId);
        var (turn2Text, _) = await StreamViaChatClientAsync(client2, threadId,
        [
            new ChatMessage(ChatRole.System, "You are a helpful assistant. Be concise."),
            new ChatMessage(ChatRole.User, "What secret word did I just tell you?")
        ]);
        _output.WriteLine($"Turn 2 (new client): '{turn2Text}'");

        Assert.False(string.IsNullOrWhiteSpace(turn2Text), "Turn 2 produced no response.");
        Assert.Contains("INDIGO5", turn2Text, StringComparison.OrdinalIgnoreCase);
    }
}


sealed class DevTemporaryTokenCredential : TokenCredential
{
    private const string EnvironmentVariable = "AZURE_BEARER_TOKEN";
    private readonly string? token = Environment.GetEnvironmentVariable(EnvironmentVariable);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetAccessToken();

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(GetAccessToken());

    private AccessToken GetAccessToken()
    {
        if (string.IsNullOrWhiteSpace(token) || string.Equals(token, nameof(DefaultAzureCredential), StringComparison.Ordinal))
        {
            throw new CredentialUnavailableException($"{EnvironmentVariable} environment variable is not set.");
        }

        return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
    }
}