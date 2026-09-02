using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using Xunit;

namespace FoundryAgentsExperiment.IntegrationTests;

[Trait("Category", "Integration")]
public class AgentChatTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string TestUserIdPrefix = "integration-test-";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private readonly ITestOutputHelper output = output;
    private readonly ConcurrentQueue<string> agentDiagnostics = new();
    private DistributedApplication? app;
    private CancellationTokenSource? resourceLogCts;
    private Task? resourceLogTask;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
        Environment.SetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES", null);

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FoundryAgentsExperiment_AppHost>(args: [], cancellationToken: cancellationToken);
        builder.Services.ConfigureHttpClientDefaults(clientBuilder => clientBuilder.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            options.CircuitBreaker.SamplingDuration = options.AttemptTimeout.Timeout * 2;
        }));

        this.app = await builder.BuildAsync(cancellationToken).WaitAsync(StartupTimeout, cancellationToken);
        await this.app.StartAsync(cancellationToken).WaitAsync(StartupTimeout, cancellationToken);
        await this.app.ResourceNotifications.WaitForResourceHealthyAsync("agent-test-sw", cancellationToken);
        await this.app.ResourceNotifications.WaitForResourceHealthyAsync("agent-dotnet", cancellationToken);

        this.resourceLogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resourceLogger = this.app.Services.GetRequiredService<ResourceLoggerService>();
        this.resourceLogTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in resourceLogger.WatchAsync("agent-dotnet").WithCancellation(this.resourceLogCts.Token))
                {
                    foreach (var (_, content, _) in batch)
                    {
                        this.agentDiagnostics.Enqueue(content);
                        this.output.WriteLine(content);
                    }
                }
            }
            catch (Exception exception) when (this.resourceLogCts.IsCancellationRequested &&
                (exception is OperationCanceledException or HttpRequestException or IOException))
            {
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        this.resourceLogCts?.Cancel();
        if (this.resourceLogTask is not null)
            await this.resourceLogTask;

        this.resourceLogCts?.Dispose();
        if (this.app is not null)
            await this.app.DisposeAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task ChatStreamExecutesServerToolAndPersistsConversation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var userId = NewUserId();
        var agent = CreateClientAgent(userId);
        var session = await agent.CreateSessionAsync(cancellationToken);

        var response = await RunTurnAsync(agent, session, "What is the current UTC date and time? Use the get_current_date_time tool.", cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(((ChatClientAgentSession)session).ConversationId));
        Assert.NotEmpty(response.Text);
        Assert.Contains(response.Contents, content => content is FunctionCallContent { Name: "get_current_date_time" });
        Assert.Contains(response.Contents, content => content is FunctionResultContent);
    }

    [Fact(Timeout = 60_000)]
    public async Task ChatStreamResumesResponseContinuation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var userId = NewUserId();
        var agent = CreateClientAgent(userId);
        var session = await agent.CreateSessionAsync(cancellationToken);
        const string firstPrompt = "Remember that my favorite color is BLUE42.";
        const string secondPrompt = "What is my favorite color?";

        await RunTurnAsync(agent, session, firstPrompt, cancellationToken);
        var firstResponseId = ((ChatClientAgentSession)session).ConversationId;
        var second = await RunTurnAsync(agent, session, secondPrompt, cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(firstResponseId));
        Assert.NotEqual(firstResponseId, ((ChatClientAgentSession)session).ConversationId);
        Assert.Contains("BLUE42", second.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 60_000)]
    public async Task LocalAgentTurnsDoNotPopulateProjectConversationHistory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var credential = new ChainedTokenCredential(new VisualStudioCredential(), new VisualStudioCodeCredential());
        var projectConnectionString = await this.app!.GetConnectionStringAsync("agent-test-sw", cancellationToken);
        var projectEndpoint = new Uri(GetConnectionStringValue(projectConnectionString!, "Endpoint"));
        var projectClient = new AIProjectClient(projectEndpoint, credential);
        var projectOpenAIClient = projectClient.ProjectOpenAIClient;
        var conversationsClient = projectOpenAIClient.GetProjectConversationsClient();
        var conversation = await conversationsClient.CreateProjectConversationAsync(cancellationToken: cancellationToken);
        var conversationId = conversation.Value.Id;

        try
        {
            var agent = CreateClientAgent(NewUserId());
            var session = await agent.CreateSessionAsync(conversationId, cancellationToken);
            await RunTurnAsync(agent, session, "Remember that my favorite color is BLUE42.", cancellationToken);

            // Create a fresh session to emulate a user returning to a conversation.
            var session2 = await agent.CreateSessionAsync(conversationId, cancellationToken);
            var second = await RunTurnAsync(agent, session2, "What is my favorite color? Reply with only the color.", cancellationToken);
            Assert.Contains("BLUE42", second.Text, StringComparison.OrdinalIgnoreCase);

            var items = new List<ResponseItem>();
            await foreach (var item in conversationsClient.GetProjectConversationItemsAsync(conversationId, cancellationToken: cancellationToken))
                items.Add(item);

            Assert.Empty(items);
        }
        finally
        {
            await conversationsClient.DeleteConversationAsync(conversationId, cancellationToken);
        }
    }

    private string NewUserId()
    {
        var userId = TestUserIdPrefix + Guid.NewGuid().ToString("N");
        return userId;
    }

    private ChatClientAgent CreateClientAgent(string userId)
    {
        var agentHost = CreateAgentHost(userId);
        var openAIOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(agentHost.BaseAddress!, "v1/"),
            Transport = new HttpClientPipelineTransport(agentHost),
        };

        return new OpenAIClient(new ApiKeyCredential("integration-test"), openAIOptions)
            .GetResponsesClient()
            .AsAIAgent(model: "agent-dotnet");
    }

    private HttpClient CreateAgentHost(string userId)
    {
        var agentHost = this.app!.CreateHttpClient("agent-dotnet");
        agentHost.DefaultRequestHeaders.Add("x-agent-user-id", userId);
        return agentHost;
    }

    private static string GetConnectionStringValue(string connectionString, string key)
    {
        DbConnectionStringBuilder builder = new() { ConnectionString = connectionString };
        return builder.TryGetValue(key, out object? value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidOperationException($"Connection string is missing '{key}'.");
    }

    private static async Task<StreamedChatResponse> RunTurnAsync(
        ChatClientAgent agent,
        AgentSession session,
        string message,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var contents = new List<AIContent>();
        await foreach (var update in agent.RunStreamingAsync(message, session, cancellationToken: cancellationToken))
        {
            contents.AddRange(update.Contents);
            text.Append(string.Concat(update.Contents.OfType<TextContent>().Select(content => content.Text)));
        }

        return new StreamedChatResponse(text.ToString(), contents);
    }

    private sealed record StreamedChatResponse(string Text, IReadOnlyList<AIContent> Contents);
}
