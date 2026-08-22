using AGUI.Client;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using FoundryAgentsExperiment.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
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
public class AgentChatTests(ITestOutputHelper output) : IAsyncLifetime
{
    private DistributedApplication? app;

    private readonly ITestOutputHelper output = output;

    private CancellationTokenSource? resourceLogCts;
    private readonly HashSet<string> testUserIds = [];
    private readonly ConcurrentQueue<string> agentDiagnostics = new();
    private string? cosmosConnectionString;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Aspire's Cosmos resource re-runs its ARM deployment (Bicep) on every AppHost start even when the
    // underlying account already exists, to reconcile any container/config changes (e.g. the
    // "agent-sessions" container added for CosmosAgentSessionStore) - this reconciliation deployment
    // routinely exceeds DefaultTimeout on its own, well before the agent-dotnet/agent-test-sw resources
    // even start. Give BuildAsync/StartAsync a longer budget than the per-turn DefaultTimeout used
    // elsewhere in this class.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private AIProjectClient? projectClient;

    private sealed record CreatedConversation(HttpClient Http, string ThreadId, string RunId, string Prompt);

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Prevent VS's debug-session Hot Reload/EnC support from leaking into the child
        // resource processes (agent-dotnet, web) that the AppHost spawns. These env vars are
        // inherited by every process the AppHost launches; if Visual Studio injected them for
        // the test host (because it's running under the debugger), hitting a breakpoint stalls
        // the shared Hot Reload IPC channel VS uses for all inherited child processes, which
        // surfaces as spurious "hot reload" errors even with no code changes.
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
        Environment.SetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES", null);

        var appBuilder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.FoundryAgentsExperiment_AppHost>(
                args: [],
                configureBuilder: (appOptions, hostSettings) =>
                {
                    appOptions.DisableDashboard = false;
                },
                cancellationToken: cancellationToken);

        appBuilder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);

            logging.AddFilter(appBuilder.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire", LogLevel.Debug);
        });

        appBuilder.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            // Standard resilience defaults (10s per attempt / 30s total) are far too aggressive for
            // this test suite's HTTP clients: agentic turns stream LLM completions and MCP tool/skill
            // calls (which themselves acquire tokens via VisualStudioCredential/DefaultAzureCredential)
            // that routinely take longer than that, even with no debugger attached. Give them enough
            // budget to complete rather than cascading a client-side timeout into a server-side
            // RequestAborted cancellation (surfacing as TaskCanceledException deep in the agent).
            clientBuilder.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
                options.CircuitBreaker.SamplingDuration = options.AttemptTimeout.Timeout * 2;
            });
        });

        this.app = await appBuilder.BuildAsync(cancellationToken).WaitAsync(StartupTimeout, cancellationToken);
        await this.app.StartAsync(cancellationToken).WaitAsync(StartupTimeout, cancellationToken);
        await this.app.ResourceNotifications.WaitForResourceHealthyAsync("agent-test-sw", cancellationToken);
        await this.app.ResourceNotifications.WaitForResourceHealthyAsync("agent-dotnet", cancellationToken);
        this.cosmosConnectionString = await this.app.GetConnectionStringAsync("cosmos")
            ?? throw new InvalidOperationException("No Cosmos connection string is available for integration-test cleanup.");

        // Stream the agent-dotnet resource's console logs (our [Wire]/[SessionStore] Checkpoint 1/2
        // logging) into the xUnit test output, since the Aspire dashboard doesn't surface them here.
        this.resourceLogCts = new CancellationTokenSource();
        var resourceLoggerService = this.app.Services.GetRequiredService<ResourceLoggerService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in resourceLoggerService.WatchAsync("agent-dotnet").WithCancellation(this.resourceLogCts.Token))
                {
                    foreach (var (_, content, _) in batch)
                    {
                        if (content.Contains("[Wire]", StringComparison.Ordinal) ||
                            content.Contains("[Transcript]", StringComparison.Ordinal) ||
                            content.Contains("[Model]", StringComparison.Ordinal))
                        {
                            this.agentDiagnostics.Enqueue(content);
                        }

                        this.output.WriteLine(content);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on dispose.
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        this.resourceLogCts?.Cancel();

        if (app is not null)
        {
            await app.DisposeAsync();
        }

        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await DeleteTestDataAsync(cleanupCts.Token);
    }

    // Agent talking through AG-UI interface on foundry agent. Not clear if this can use conversations via openAI?
    private ChatClientAgent CreateAGUIAgent(string userId, IList<AITool>? tools = null)
    {
        TrackTestUser(userId);
        var http = app!.CreateHttpClient("agent-dotnet");
        http.DefaultRequestHeaders.Add("x-agent-user-id", userId);

        return new AGUIChatClient(new AGUIChatClientOptions(http, "/ag-ui"))
            .AsAIAgent(name: "agui-client", description: "AG-UI Client Agent", tools: tools);
    }

    private HttpClient CreateAgentHttpClient(string userId)
    {
        TrackTestUser(userId);
        var http = app!.CreateHttpClient("agent-dotnet");
        http.DefaultRequestHeaders.Add("x-agent-user-id", userId);
        return http;
    }

    private void TrackTestUser(string userId)
    {
        if (!userId.StartsWith("test-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Integration-test cleanup only supports generated test user IDs.");
        }

        testUserIds.Add(userId);
    }

    private async Task DeleteTestDataAsync(CancellationToken cancellationToken)
    {
        if (testUserIds.Count == 0 || cosmosConnectionString is null)
        {
            return;
        }

        using var cosmosClient = new CosmosClient(cosmosConnectionString, GetCredential());
        var database = cosmosClient.GetDatabase("agent-history");

        foreach (var userId in testUserIds)
        {
            await DeleteTranscriptAsync(database.GetContainer("agent-transcript"), userId, cancellationToken);
            await DeleteUserPartitionAsync(database.GetContainer("agent-sessions"), userId, cancellationToken);
            await DeleteUserPartitionAsync(database.GetContainer("conversation-index"), userId, cancellationToken);
        }
    }

    private static async Task DeleteTranscriptAsync(
        Container container,
        string userId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT c.id, c.tenantId, c.conversationId FROM c WHERE c.userId = @userId")
            .WithParameter("@userId", userId);
        using var iterator = container.GetItemQueryIterator<ChatHistoryItemId>(query);

        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                var partitionKey = new PartitionKeyBuilder()
                    .Add(item.TenantId)
                    .Add(userId)
                    .Add(item.ConversationId)
                    .Build();
                await container.DeleteItemAsync<ChatHistoryItemId>(item.Id, partitionKey, cancellationToken: cancellationToken);
            }
        }
    }

    private static async Task DeleteUserPartitionAsync(Container container, string userId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("SELECT c.id FROM c WHERE c.userId = @userId")
            .WithParameter("@userId", userId);
        using var iterator = container.GetItemQueryIterator<CosmosItemId>(query);

        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                await container.DeleteItemAsync<CosmosItemId>(item.Id, new PartitionKey(userId), cancellationToken: cancellationToken);
            }
        }
    }

    private sealed record ChatHistoryItemId(string Id, string TenantId, string ConversationId);
    private sealed record CosmosItemId(string Id);

    private async Task<CreatedConversation> CreateConversationAsync(string userId, CancellationToken cancellationToken)
    {
        var agent = CreateAGUIAgent(userId);
        var session = await agent.CreateSessionAsync(cancellationToken);
        var continuation = new AGUIContinuationState();
        Assert.Null(continuation.CreateRunOptions());
        var prompt = $"Conversation endpoint integration test {Guid.NewGuid():N}.";
        List<ChatMessage> messages = [CreateUserMessage(prompt)];

        await foreach (var update in agent.RunStreamingAsync(messages, session, continuation.CreateRunOptions(), cancellationToken))
        {
            continuation.Observe(update);
        }

        if (continuation.ThreadId is not { Length: > 0 } threadId ||
            continuation.PreviousRunId is not { Length: > 0 } runId)
        {
            throw new InvalidOperationException("The AG-UI run did not return continuation state.");
        }

        var http = CreateAgentHttpClient(userId);
        List<ConversationSummary> lastConversations = [];
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var conversations = await http.GetFromJsonAsync<List<ConversationSummary>>("/conversations", cancellationToken) ?? [];
            lastConversations = conversations;
            if (conversations.Any(conversation => conversation.Id == threadId))
            {
                return new CreatedConversation(http, threadId, runId, prompt);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        http.Dispose();
        output.WriteLine($"Expected AG-UI thread '{threadId}'; observed index IDs: {string.Join(", ", lastConversations.Select(conversation => conversation.Id))}");
        throw new InvalidOperationException($"Conversation index did not contain AG-UI thread '{threadId}' after the run completed. Observed index IDs: {string.Join(", ", lastConversations.Select(conversation => conversation.Id))}");
    }

    private async Task<AIProjectClient> CreateProjectClientAsync()
    {
        var endPoint = await app!.GetConnectionStringAsync("agent-test-sw") ?? throw new InvalidOperationException("No connection string to foundry");
        var endPointUri = new Uri(endPoint.Replace("Endpoint=", ""));
        this.projectClient = new AIProjectClient(endPointUri, GetCredential());

        return this.projectClient;
    }

    [Fact]
    public async Task AGUIHistoryDiagnosticsCaptureTwoTurnToolConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var session = await agent.CreateSessionAsync(ct);
        var continuation = new AGUIContinuationState();
        Assert.Null(continuation.CreateRunOptions());

        await foreach (var update in agent.RunStreamingAsync(
            [CreateUserMessage("Good evening.")],
            session,
            continuation.CreateRunOptions(),
            ct))
        {
            continuation.Observe(update);
        }

        await foreach (var update in agent.RunStreamingAsync(
            [CreateUserMessage("Use your silly-math skill to calculate 6 * 7.")],
            session,
            continuation.CreateRunOptions(),
            ct))
        {
            continuation.Observe(update);
        }

        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId));

        using var http = CreateAgentHttpClient(userId);
        var conversation = await http.GetFromJsonAsync<ConversationDetail>(
            $"/conversations/{Uri.EscapeDataString(continuation.ThreadId!)}",
            ct);

        Assert.NotNull(conversation);
        Assert.Contains(conversation.Messages, message => message.Text.Contains("Good evening", StringComparison.Ordinal));
        Assert.Contains(conversation.Messages, message => message.Text.Contains("6 * 7", StringComparison.Ordinal));
        Assert.DoesNotContain(conversation.Messages, message => message.Text.StartsWith("## Memories", StringComparison.Ordinal));
        Assert.DoesNotContain(conversation.Messages, message => message.Role == ChatRole.Assistant &&
            string.IsNullOrWhiteSpace(message.Text) &&
            !message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent));
        Assert.Equal(1, conversation.Messages.Count(message => message.Text == "Good evening."));
        var skillPromptMessages = conversation.Messages
            .Where(message => message.Text.Contains("6 * 7", StringComparison.Ordinal))
            .ToList();
        Assert.True(skillPromptMessages.Count == 1,
            $"Expected one persisted skill prompt but found {skillPromptMessages.Count}: {string.Join(", ", skillPromptMessages.Select(message => message.MessageId ?? "<null>"))}");
        Assert.Contains(conversation.Messages, message =>
            message.Role == ChatRole.Assistant &&
            message.Contents.Any(content => content is FunctionCallContent));
        Assert.Contains(conversation.Messages, message =>
            message.Role == ChatRole.Tool &&
            message.Contents.Any(content => content is FunctionResultContent));
    }

    [Fact]
    public async Task AGUIPreservesClientAssignedUserMessageIdThroughToolLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var session = await agent.CreateSessionAsync(ct);
        var continuation = new AGUIContinuationState();
        Assert.Null(continuation.CreateRunOptions());
        var messageId = "client-" + Guid.NewGuid().ToString("N");
        var prompt = "Use your silly-math skill to calculate 6 * 7.";

        await foreach (var update in agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, prompt) { MessageId = messageId }],
            session,
            continuation.CreateRunOptions(),
            ct))
        {
            continuation.Observe(update);
        }

        using var http = CreateAgentHttpClient(userId);
        var conversation = await http.GetFromJsonAsync<ConversationDetail>(
            $"/conversations/{Uri.EscapeDataString(continuation.ThreadId!)}",
            ct);

        Assert.NotNull(conversation);
        var recalledUserMessage = Assert.Single(conversation.Messages, message => message.Text == prompt);
        Assert.Equal(messageId, recalledUserMessage.MessageId);
    }

    [Fact]
    public async Task AGUIClientToolContinuationsDoNotDuplicateTranscriptHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        AITool[] clientTools =
        [
            AIFunctionFactory.Create(() => "51.3967°N, -1.3172°E", name: "get_user_location")
        ];
        var agent = CreateAGUIAgent(userId, clientTools);
        var session = await agent.CreateSessionAsync(ct);
        var continuation = new AGUIContinuationState();
        Assert.Null(continuation.CreateRunOptions());

        var firstTurnResults = await RunLocationTurnAsync(agent, session, continuation, "Where am I?", ct);
        var secondTurnResults = await RunLocationTurnAsync(agent, session, continuation, "Where am I now?", ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var streamedToolResults = firstTurnResults.Concat(secondTurnResults).ToList();
        var streamedToolResultSummary = string.Join(
            ", ",
            streamedToolResults.Select(result => $"{result.CallId}={result.Result}"));
        output.WriteLine($"Location-tool results={streamedToolResultSummary}");
        Assert.True(
            firstTurnResults.Count == 1,
            $"Expected one first-turn tool result but received {firstTurnResults.Count}:{Environment.NewLine}" +
            GetAgentDiagnostics());
        Assert.True(
            secondTurnResults.Count == 1,
            $"Expected one second-turn tool result but received {secondTurnResults.Count}:{Environment.NewLine}" +
            GetAgentDiagnostics());
        Assert.NotEqual(firstTurnResults[0].CallId, secondTurnResults[0].CallId);
        Assert.Equal(
            streamedToolResults.Count,
            streamedToolResults.Select(result => result.CallId).Distinct(StringComparer.Ordinal).Count());

        using var http = CreateAgentHttpClient(userId);
        var conversation = await http.GetFromJsonAsync<ConversationDetail>(
            $"/conversations/{Uri.EscapeDataString(continuation.ThreadId!)}",
            ct);

        Assert.NotNull(conversation);
        var recalledMessageSummary = string.Join(Environment.NewLine, conversation.Messages.Select(message =>
            $"{message.Role}: {string.Join(", ", message.Contents.Select(content => content switch
            {
                FunctionCallContent call => $"FunctionCall({call.CallId})",
                FunctionResultContent result => $"FunctionResult({result.CallId}, {result.Result})",
                _ => content.GetType().Name,
            }))}"));
        Assert.True(
            conversation.Messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any()),
            $"Recalled messages:{Environment.NewLine}{recalledMessageSummary}");
        var persistedToolResults = conversation.Messages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .ToList();
        Assert.Equal(
            persistedToolResults.Count,
            persistedToolResults.Select(result => result.CallId).Distinct(StringComparer.Ordinal).Count());

        var persistedFunctionCallIds = conversation.Messages
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            persistedToolResults,
            result => Assert.Contains(result.CallId, persistedFunctionCallIds));
        Assert.Equal(2, persistedToolResults.Count);
    }

    private static async Task<List<FunctionResultContent>> RunLocationTurnAsync(
        ChatClientAgent agent,
        AgentSession session,
        AGUIContinuationState continuation,
        string prompt,
        CancellationToken cancellationToken)
    {
        List<FunctionResultContent> toolResults = [];
        await foreach (var update in agent.RunStreamingAsync(
            [CreateUserMessage(prompt)],
            session,
            continuation.CreateRunOptions(),
            cancellationToken))
        {
            continuation.Observe(update);
            toolResults.AddRange(update.Contents.OfType<FunctionResultContent>());
        }

        return toolResults;
    }

    private string GetAgentDiagnostics() =>
        string.Join(Environment.NewLine, agentDiagnostics.TakeLast(100));

    [Fact]
    public async Task ConversationListPersistsAGUIRunContinuation()
    {
        var created = await CreateConversationAsync(
            "test-" + Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
        using var http = created.Http;

        using var updateResponse = await http.PutAsJsonAsync(
            $"/conversations/{Uri.EscapeDataString(created.ThreadId)}/continuation",
            new { runId = created.RunId, initialUserPrompt = created.Prompt },
            TestContext.Current.CancellationToken);
        updateResponse.EnsureSuccessStatusCode();

        var conversations = await http.GetFromJsonAsync<List<ConversationSummary>>(
            "/conversations",
            TestContext.Current.CancellationToken);
        var conversation = Assert.Single(conversations!, item => item.Id == created.ThreadId);

        Assert.Equal(created.RunId, conversation.LastRunId);
        Assert.StartsWith("Conversation endpoint integration test", conversation.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationResumeReturnsTranscriptAndEnforcesOwnership()
    {
        var created = await CreateConversationAsync(
            "test-" + Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
        using var ownerHttp = created.Http;

        using var updateResponse = await ownerHttp.PutAsJsonAsync(
            $"/conversations/{Uri.EscapeDataString(created.ThreadId)}/continuation",
            new { runId = created.RunId, initialUserPrompt = created.Prompt },
            TestContext.Current.CancellationToken);
        updateResponse.EnsureSuccessStatusCode();

        var conversation = await ownerHttp.GetFromJsonAsync<ConversationDetail>(
            $"/conversations/{Uri.EscapeDataString(created.ThreadId)}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(conversation);
        Assert.Equal(created.ThreadId, conversation.ConversationId);
        Assert.Equal(created.RunId, conversation.LastRunId);
        Assert.Contains(conversation.Messages,
            message => message.Role == ChatRole.User &&
                       message.Text?.Contains(created.Prompt, StringComparison.Ordinal) == true);

        using var otherUserHttp = CreateAgentHttpClient("test-" + Guid.NewGuid().ToString("N"));
        using var otherUserResponse = await otherUserHttp.GetAsync(
            $"/conversations/{Uri.EscapeDataString(created.ThreadId)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, otherUserResponse.StatusCode);
    }

    internal static TokenCredential GetCredential() =>
             new ChainedTokenCredential(
                new VisualStudioCredential(),
                new VisualStudioCodeCredential(),
                new DefaultAzureCredential());

    private static ChatMessage CreateUserMessage(string text) =>
        new(ChatRole.User, text)
        {
            MessageId = Guid.NewGuid().ToString("N"),
        };

    [Fact]
    public async Task AGUIAgentRecallsFact1()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var projectClient = await CreateProjectClientAsync();

        AgentSession session = await agent.CreateSessionAsync(ct);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are a helpful assistant.")
        ];

        var continuation = new AGUIContinuationState();

        messages.Add(CreateUserMessage("My favourite colour is BLUE42. This is important, remember it."));

        string response1 = string.Empty;
        string errorMessage = string.Empty;

        // Stream the response.
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session, continuation.CreateRunOptions(), ct))
        {
            continuation.Observe(update);

            // Display streaming text content
            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    response1 += textContent.Text;
                }
                else if (content is ErrorContent errorContent)
                {
                    errorMessage = errorContent.Message;
                }
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId), "No thread ID returned from turn 1.");
        Assert.False(string.IsNullOrWhiteSpace(continuation.PreviousRunId), "No run ID returned from turn 1.");

        messages = [CreateUserMessage("What is my favourite colour?")];
        string response2 = string.Empty;
        string errorMessage2 = string.Empty;

        // Stream the response.
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session, continuation.CreateRunOptions(), ct))
        {
            continuation.Observe(update);

            // Display streaming text content
            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    response2 += textContent.Text;
                }
                else if (content is ErrorContent errorContent)
                {
                    errorMessage2 = errorContent.Message;
                }
            }
        }

        // This is now passing with the magical session store configuration.
        Assert.Contains("BLUE42", response2, StringComparison.OrdinalIgnoreCase);

        // On to the next test, can it recall something with memory in a new conversation?
        // Need to wait for the memory to land (not sure how long that might take?)        
        bool foundMemory = false;
        int retryCount = 0;
        int retryLimit = 5;
        int retryDelayMs = 1000;

        while (foundMemory == false && retryCount < retryLimit)
        {
            var memories = await projectClient.MemoryStores.GetMemoriesAsync("agent-dotnet-memory", userId, cancellationToken: ct)
                .ToListAsync(cancellationToken: ct);

            if (memories.Count != 0)
            {
                foundMemory = true;
                var relevantMemory = memories.FirstOrDefault(m => m.Content.Contains("BLUE42", StringComparison.OrdinalIgnoreCase));
                Assert.True(relevantMemory != null);
                output.WriteLine($"Found relevant memory: {relevantMemory.Content}");
                break;
            }

            await Task.Delay(retryDelayMs, ct);
            retryCount++;
        }

        Assert.True(foundMemory, "Memory not found in memory store after retries.");

        AgentSession session3 = await agent.CreateSessionAsync(ct);
        messages = [CreateUserMessage("Do you remember my favourite colour?")];

        string response3 = string.Empty;
        string errorMessage3 = string.Empty;

        // Stream the response.
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session3, cancellationToken: ct))
        {
            // Display streaming text content
            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    response3 += textContent.Text;
                }
                else if (content is ErrorContent errorContent)
                {
                    errorMessage3 = errorContent.Message;
                }
            }
        }

        // Does it remember without the chat thread? Assumes the memory provider is working with the user id.
        Assert.Contains("BLUE42", response3, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGUIAgentCanUseSkill()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var projectClient = await CreateProjectClientAsync();

        AgentSession session = await agent.CreateSessionAsync(ct);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are a helpful assistant.")
        ];

        var continuation = new AGUIContinuationState();

        messages.Add(CreateUserMessage("Use your silly-math skill to calculate 6 * 7."));

        string response1 = string.Empty;
        string errorMessage = string.Empty;

        // Stream the response.
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session, continuation.CreateRunOptions(), ct))
        {
            continuation.Observe(update);

            // Display streaming text content
            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    response1 += textContent.Text;
                }
                else if (content is ErrorContent errorContent)
                {
                    errorMessage = errorContent.Message;
                }
            }
        }

        Assert.Contains("42", response1, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId), "No thread ID returned from the skill run.");
        Assert.False(string.IsNullOrWhiteSpace(continuation.PreviousRunId), "No run ID returned from the skill run.");
    }
}