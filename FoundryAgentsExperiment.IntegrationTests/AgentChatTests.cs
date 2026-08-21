using AGUI.Client;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.Core;
using Azure.Identity;
using FoundryAgentsExperiment.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    private string? previousCompactionTriggerTokens;
    private string? previousCompactionMinimumPreservedGroups;
    private string? previousEnableFoundryMemory;

    private const string CompactionTriggerTokensEnvironmentVariable = "SessionPersistence__CompactionTriggerTokens";
    private const string CompactionMinimumPreservedGroupsEnvironmentVariable = "SessionPersistence__CompactionMinimumPreservedGroups";
    private const string EnableFoundryMemoryEnvironmentVariable = "EnableFoundryMemory";

    // Aspire's Cosmos resource re-runs its ARM deployment (Bicep) on every AppHost start even when the
    // underlying account already exists, to reconcile any container/config changes (e.g. the
    // "agent-sessions" container added for CosmosAgentSessionStore) - this reconciliation deployment
    // routinely exceeds the standard per-turn timeout on its own, well before the agent-dotnet/agent-test-sw
    // resources even start. Give BuildAsync/StartAsync a longer budget.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);

    private sealed record CreatedConversation(HttpClient Http, string ThreadId, string Prompt);
    private sealed record PersistedSessionId(string Id);
    private sealed record PersistedSessionDocument(string SerializedSession);
    private sealed record PersistedHistoryMessage(string Role, string? MessageId, string Text);

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
        this.previousCompactionTriggerTokens = Environment.GetEnvironmentVariable(CompactionTriggerTokensEnvironmentVariable);
        this.previousCompactionMinimumPreservedGroups = Environment.GetEnvironmentVariable(CompactionMinimumPreservedGroupsEnvironmentVariable);
        this.previousEnableFoundryMemory = Environment.GetEnvironmentVariable(EnableFoundryMemoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(CompactionTriggerTokensEnvironmentVariable, "512");
        Environment.SetEnvironmentVariable(CompactionMinimumPreservedGroupsEnvironmentVariable, "2");
        Environment.SetEnvironmentVariable(EnableFoundryMemoryEnvironmentVariable, "true");

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

        // Stream agent diagnostics into xUnit output because the Aspire dashboard does not surface them here.
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
                        this.agentDiagnostics.Enqueue(content);
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

        Environment.SetEnvironmentVariable(CompactionTriggerTokensEnvironmentVariable, this.previousCompactionTriggerTokens);
        Environment.SetEnvironmentVariable(CompactionMinimumPreservedGroupsEnvironmentVariable, this.previousCompactionMinimumPreservedGroups);
        Environment.SetEnvironmentVariable(EnableFoundryMemoryEnvironmentVariable, this.previousEnableFoundryMemory);

        await DeleteTestSessionsAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AGUIHistoryDiagnosticsCaptureTwoTurnToolConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var continuation = new AGUIContinuationState();

        List<ChatMessage> firstTurnMessages = [CreateUserMessage("Good evening.")];
        var firstTurnSession = await agent.CreateSessionAsync(ct);
        await foreach (var update in agent.RunStreamingAsync(
            firstTurnMessages,
            firstTurnSession,
            options: continuation.CreateRunOptions(),
            cancellationToken: ct))
        {
            continuation.Observe(update);
        }

        List<ChatMessage> secondTurnMessages = [CreateUserMessage("Use your silly-math skill to calculate 6 * 7.")];
        var secondTurnSession = await agent.CreateSessionAsync(ct);
        await foreach (var update in agent.RunStreamingAsync(
            secondTurnMessages,
            secondTurnSession,
            options: continuation.CreateRunOptions(),
            cancellationToken: ct))
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
        Assert.DoesNotContain(conversation.Messages, message => message.Role == ChatRole.Assistant &&
            string.IsNullOrWhiteSpace(message.Text) &&
            !message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent));
        Assert.Equal(1, conversation.Messages.Count(message => message.Text == "Good evening."));
        var skillPromptMessages = conversation.Messages
            .Where(message => message.Role == ChatRole.User && message.Text == secondTurnMessages[0].Text)
            .ToList();
        Assert.Single(skillPromptMessages);
        Assert.Contains(conversation.Messages, message =>
            message.Role == ChatRole.Assistant &&
            message.Contents.Any(content => content is FunctionCallContent));
        Assert.Contains(conversation.Messages, message =>
            message.Role == ChatRole.Tool &&
            message.Contents.Any(content => content is FunctionResultContent));
    }

    [Fact]
    public async Task AGUICompactionPersistsStateAndPreservesRecentHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var continuation = new AGUIContinuationState();
        var earlyFact = "The archive access phrase is AURORA-913.";
        var longContext = string.Join(' ', Enumerable.Repeat("This is background context for a planning discussion.", 90));

        await RunTurnAsync(agent, continuation, userId, $"{earlyFact} {longContext}", ct);
        await RunTurnAsync(agent, continuation, userId, "The most recent meeting room is Cedar-204. Remember this exactly.", ct);
        var recallResponse = await RunTurnAsync(
            agent,
            continuation,
            userId,
            "What is the archive access phrase and what is the most recent meeting room?",
            ct);

        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId));
        Assert.Contains("AURORA-913", recallResponse, StringComparison.OrdinalIgnoreCase);

        using var cosmosClient = new CosmosClient(cosmosConnectionString, GetCredential());
        var container = cosmosClient.GetContainer("agent-history", "agent-sessions");
        var response = await container.ReadItemAsync<PersistedSessionDocument>(
            continuation.ThreadId!,
            new PartitionKey(userId),
            cancellationToken: ct);
        using var persistedSession = JsonDocument.Parse(response.Resource.SerializedSession);
        var stateBag = persistedSession.RootElement.GetProperty("stateBag");
        Assert.True(
            stateBag.TryGetProperty("SummarizationCompactionStrategy", out var compactionState) &&
            compactionState.ValueKind == JsonValueKind.Object,
            $"The persisted session did not contain compaction state.{Environment.NewLine}{response.Resource.SerializedSession}");

        var persistedHistory = await ReadPersistedHistoryAsync(userId, continuation.ThreadId!, "after compaction", ct);
        Assert.Contains(
            persistedHistory,
            message => message.Role == "user" && message.Text.Contains("Cedar-204", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGUIPreservesClientAssignedUserMessageIdThroughToolLoop()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var session = await agent.CreateSessionAsync(ct);
        var continuation = new AGUIContinuationState();
        var messageId = "client-" + Guid.NewGuid().ToString("N");
        var prompt = "Use your silly-math skill to calculate 6 * 7.";

        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, prompt) { MessageId = messageId }];
        await foreach (var update in agent.RunStreamingAsync(
            messages,
            session,
            options: continuation.CreateRunOptions(),
            cancellationToken: ct))
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
    public async Task AGUIClientToolContinuationsPersistCompleteSessionHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        AITool[] clientTools =
        [
            AIFunctionFactory.Create(() => "51.3967°N, -1.3172°E", name: "get_user_location")
        ];
        var agent = CreateAGUIAgent(userId, clientTools);
        var continuation = new AGUIContinuationState();

        var firstTurnResults = await RunLocationTurnAsync(agent, continuation, userId, "Where am I?", ct);
        var secondTurnResults = await RunLocationTurnAsync(agent, continuation, userId, "Where am I now?", ct);
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
        Assert.True(
            persistedToolResults.Count == persistedToolResults.Select(result => result.CallId).Distinct(StringComparer.Ordinal).Count(),
            $"Persisted tool result call IDs are duplicated.{Environment.NewLine}" +
            $"Recalled messages:{Environment.NewLine}{recalledMessageSummary}");

        var persistedFunctionCallIds = conversation.Messages
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            persistedToolResults,
            result => Assert.Contains(result.CallId, persistedFunctionCallIds));
        Assert.True(
            persistedToolResults.Count == 2,
            $"Expected two persisted tool results but found {persistedToolResults.Count}.{Environment.NewLine}" +
            $"Recalled messages:{Environment.NewLine}{recalledMessageSummary}");
    }

    [Fact]
    public async Task AGUIClientToolContinuationPersistsEachProtocolMessageOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        AITool[] clientTools =
        [
            AIFunctionFactory.Create(() => "51.3967°N, -1.3172°E", name: "get_user_location")
        ];
        var agent = CreateAGUIAgent(userId, clientTools);
        var continuation = new AGUIContinuationState();
        const string prompt = "Where am I?";

        var streamedToolResults = await RunLocationTurnAsync(agent, continuation, userId, prompt, ct);
        var streamedToolResult = Assert.Single(streamedToolResults);
        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId));

        using var persistedSession = await ReadPersistedSessionAsync(userId, continuation.ThreadId!, ct);
        var stateBag = persistedSession.RootElement.GetProperty("stateBag");
        var messages = stateBag
            .GetProperty("InMemoryChatHistoryProvider")
            .GetProperty("messages")
            .EnumerateArray()
            .ToArray();

        var persistedUserMessages = messages
            .Where(message =>
                message.GetProperty("role").GetString() == "user" &&
                message.GetProperty("contents").EnumerateArray().Any(content =>
                    content.TryGetProperty("text", out var text) && text.GetString() == prompt))
            .ToList();
        Assert.True(
            persistedUserMessages.Count == 1,
            $"Expected the client user message once but found {persistedUserMessages.Count}.{Environment.NewLine}" +
            DescribeRawPersistedHistory(messages));

        var functionCalls = messages
            .SelectMany(message => message.GetProperty("contents").EnumerateArray())
            .Where(content => content.TryGetProperty("$type", out var type) && type.GetString() == "functionCall")
            .ToList();
        var functionCall = Assert.Single(functionCalls);
        Assert.Equal(streamedToolResult.CallId, functionCall.GetProperty("callId").GetString());

        var functionResults = messages
            .SelectMany(message => message.GetProperty("contents").EnumerateArray())
            .Where(content => content.TryGetProperty("$type", out var type) && type.GetString() == "functionResult")
            .ToList();
        var functionResult = Assert.Single(functionResults);
        Assert.Equal(streamedToolResult.CallId, functionResult.GetProperty("callId").GetString());
        Assert.False(ContainsPendingToolApprovalRequest(stateBag), DescribeRawPersistedHistory(messages));
    }

    [Fact]
    public async Task FoundryMemorySearchContextIsNotPersistedForOrdinaryConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var continuation = new AGUIContinuationState();
        var fact = $"My unique memory marker is cobalt-{Guid.NewGuid():N}.";

        await RunTurnAsync(agent, continuation, userId, fact, ct);
        await RunTurnAsync(agent, continuation, userId, "What is my unique memory marker?", ct);
        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId));

        using var persistedSession = await ReadPersistedSessionAsync(userId, continuation.ThreadId!, ct);
        var stateBag = persistedSession.RootElement.GetProperty("stateBag");
        Assert.False(
            ContainsFoundryMemoryContext(stateBag),
            $"Foundry memory search context was persisted in the server session.{Environment.NewLine}{stateBag.GetRawText()}");
    }

    [Fact]
    public async Task ConversationListPersistsAGUIRunContinuation()
    {
        var created = await CreateConversationAsync(
            "test-" + Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
        using var http = created.Http;

        var conversations = await http.GetFromJsonAsync<List<ConversationSummary>>(
            "/conversations",
            TestContext.Current.CancellationToken);
        var conversation = Assert.Single(conversations!, item => item.Id == created.ThreadId);

        Assert.StartsWith("Conversation endpoint integration test", conversation.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationResumeReturnsTranscriptAndEnforcesOwnership()
    {
        var created = await CreateConversationAsync(
            "test-" + Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
        using var ownerHttp = created.Http;

        var conversation = await ownerHttp.GetFromJsonAsync<ConversationDetail>(
            $"/conversations/{Uri.EscapeDataString(created.ThreadId)}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(conversation);
        Assert.Equal(created.ThreadId, conversation.ConversationId);
        Assert.Contains(conversation.Messages,
            message => message.Role == ChatRole.User &&
                       message.Text?.Contains(created.Prompt, StringComparison.Ordinal) == true);

        using var otherUserHttp = CreateAgentHttpClient("test-" + Guid.NewGuid().ToString("N"));
        using var otherUserResponse = await otherUserHttp.GetAsync(
            $"/conversations/{Uri.EscapeDataString(created.ThreadId)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, otherUserResponse.StatusCode);
    }

    [Fact]
    public async Task AGUIAgentRecallsFact1()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var continuation = new AGUIContinuationState();
        List<ChatMessage> messages = [CreateUserMessage("My favourite colour is BLUE42. This is important, remember it.")];

        var firstTurnSession = await agent.CreateSessionAsync(ct);
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            messages,
            firstTurnSession,
            options: continuation.CreateRunOptions(),
            cancellationToken: ct))
        {
            continuation.Observe(update);

        }

        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId), "No thread ID returned from turn 1.");
        Assert.False(string.IsNullOrWhiteSpace(continuation.PreviousRunId), "No run ID returned from turn 1.");
        var firstTurnHistory = await ReadPersistedHistoryAsync(userId, continuation.ThreadId!, "after turn 1", ct);
        Assert.True(
            firstTurnHistory.Count(message => message.Role == "user" && message.Text.Contains("BLUE42", StringComparison.Ordinal)) == 1,
            DescribePersistedHistory("after turn 1", firstTurnHistory));
        var secondTurnSession = await agent.CreateSessionAsync(ct);

        messages = [CreateUserMessage("What is my favourite colour?")];
        string response2 = string.Empty;

        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            messages,
            secondTurnSession,
            options: continuation.CreateRunOptions(),
            cancellationToken: ct))
        {
            continuation.Observe(update);

            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    response2 += textContent.Text;
                }
            }
        }

        var secondTurnHistory = await ReadPersistedHistoryAsync(userId, continuation.ThreadId!, "after turn 2", ct);
        Assert.True(
            secondTurnHistory.Count(message => message.Role == "user" && message.Text.Contains("BLUE42", StringComparison.Ordinal)) == 1,
            DescribePersistedHistory("after turn 2", secondTurnHistory));

        Assert.Contains("BLUE42", response2, StringComparison.OrdinalIgnoreCase);

        // On to the next test, can it recall something with memory in a new conversation?
        // Need to wait for the memory to land (not sure how long that might take?)        
        //bool foundMemory = false;
        //int retryCount = 0;
        //int retryLimit = 5;
        //int retryDelayMs = 1000;

        //while (foundMemory == false && retryCount < retryLimit)
        //{
        //    var memories = await projectClient.MemoryStores.GetMemoriesAsync("agent-dotnet-memory", userId, cancellationToken: ct)
        //        .ToListAsync(cancellationToken: ct);

        //    if (memories.Count != 0)
        //    {
        //        foundMemory = true;
        //        var relevantMemory = memories.FirstOrDefault(m => m.Content.Contains("BLUE42", StringComparison.OrdinalIgnoreCase));
        //        Assert.True(relevantMemory != null);
        //        output.WriteLine($"Found relevant memory: {relevantMemory.Content}");
        //        break;
        //    }

        //    await Task.Delay(retryDelayMs, ct);
        //    retryCount++;
        //}

        //Assert.True(foundMemory, "Memory not found in memory store after retries.");

        //AgentSession session3 = await agent.CreateSessionAsync(ct);
        //messages = [CreateUserMessage("Do you remember my favourite colour?")];

        //string response3 = string.Empty;
        //string errorMessage3 = string.Empty;

        //// Stream the response.
        //await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session3, cancellationToken: ct))
        //{
        //    // Display streaming text content
        //    foreach (AIContent content in update.Contents)
        //    {
        //        if (content is TextContent textContent)
        //        {
        //            response3 += textContent.Text;
        //        }
        //        else if (content is ErrorContent errorContent)
        //        {
        //            errorMessage3 = errorContent.Message;
        //        }
        //    }
        //}

        //// Does it remember without the chat thread? Assumes the memory provider is working with the user id.
        //Assert.Contains("BLUE42", response3, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGUIAgentCanUseSkill()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = "test-" + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        AgentSession session = await agent.CreateSessionAsync(ct);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are a helpful assistant.")
        ];

        var continuation = new AGUIContinuationState();

        messages.Add(CreateUserMessage("Use your silly-math skill to calculate 6 * 7."));

        string response1 = string.Empty;
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            messages,
            session,
            options: continuation.CreateRunOptions(),
            cancellationToken: ct))
        {
            continuation.Observe(update);

            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    response1 += textContent.Text;
                }
            }
        }

        Assert.Contains("42", response1, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId), "No thread ID returned from the skill run.");
        Assert.False(string.IsNullOrWhiteSpace(continuation.PreviousRunId), "No run ID returned from the skill run.");
    }

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
            throw new InvalidOperationException("Only generated test user IDs may be cleaned up.");

        testUserIds.Add(userId);
    }

    private async Task DeleteTestSessionsAsync(CancellationToken cancellationToken)
    {
        if (testUserIds.Count == 0 || cosmosConnectionString is null)
            return;

        using var cosmosClient = new CosmosClient(cosmosConnectionString, GetCredential());
        var container = cosmosClient.GetContainer("agent-history", "agent-sessions");

        foreach (var userId in testUserIds)
        {
            var query = new QueryDefinition("SELECT c.id FROM c WHERE c.userId = @userId")
                .WithParameter("@userId", userId);
            using var iterator = container.GetItemQueryIterator<PersistedSessionId>(query);

            while (iterator.HasMoreResults)
            {
                foreach (var session in await iterator.ReadNextAsync(cancellationToken))
                {
                    await container.DeleteItemAsync<PersistedSessionId>(
                        session.Id,
                        new PartitionKey(userId),
                        cancellationToken: cancellationToken);
                }
            }
        }
    }

    private async Task<CreatedConversation> CreateConversationAsync(string userId, CancellationToken cancellationToken)
    {
        var agent = CreateAGUIAgent(userId);
        // The client session is used only while handling this one user message. The hosted Cosmos
        // session owns conversation history and is resumed on later messages through run options.
        var session = await agent.CreateSessionAsync(cancellationToken);
        var continuation = new AGUIContinuationState();
        var prompt = $"Conversation endpoint integration test {Guid.NewGuid():N}.";
        List<ChatMessage> messages = [CreateUserMessage(prompt)];

        await foreach (var update in agent.RunStreamingAsync(
            messages,
            session,
            options: continuation.CreateRunOptions(),
            cancellationToken: cancellationToken))
        {
            continuation.Observe(update);
        }

        if (continuation.ThreadId is not { Length: > 0 } threadId ||
            continuation.PreviousRunId is not { Length: > 0 })
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
                return new CreatedConversation(http, threadId, prompt);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        http.Dispose();
        output.WriteLine($"Expected AG-UI thread '{threadId}'; observed session IDs: {string.Join(", ", lastConversations.Select(conversation => conversation.Id))}");
        throw new InvalidOperationException($"Session store did not contain AG-UI thread '{threadId}' after the run completed. Observed session IDs: {string.Join(", ", lastConversations.Select(conversation => conversation.Id))}");
    }

    private static ChatMessage CreateUserMessage(string text) =>
        new(ChatRole.User, text)
        {
            MessageId = Guid.NewGuid().ToString("N"),
        };

    private async Task<string> RunTurnAsync(
        ChatClientAgent agent,
        AGUIContinuationState continuation,
        string userId,
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = new StringBuilder();
            var session = await agent.CreateSessionAsync(cancellationToken);
            List<ChatMessage> messages = [CreateUserMessage(prompt)];

            await foreach (var update in agent.RunStreamingAsync(
                messages,
                session,
                options: continuation.CreateRunOptions(),
                cancellationToken: cancellationToken))
            {
                continuation.Observe(update);
                response.Append(string.Concat(update.Contents.OfType<TextContent>().Select(content => content.Text)));
            }

            return response.ToString();
        }
        catch (HttpRequestException exception)
        {
            using var diagnosticsHttp = CreateAgentHttpClient(userId);
            using var diagnosticResponse = await diagnosticsHttp.GetAsync("/_diagnostics/ag-ui-failure", cancellationToken);
            var serverFailure = await diagnosticResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Compaction turn failed for prompt '{prompt}'. Diagnostic status={(int)diagnosticResponse.StatusCode} {diagnosticResponse.ReasonPhrase}.{Environment.NewLine}Server failure:{Environment.NewLine}{serverFailure}{Environment.NewLine}Agent diagnostics:{Environment.NewLine}{GetAgentDiagnostics()}",
                exception);
        }
    }

    private async Task<IReadOnlyList<PersistedHistoryMessage>> ReadPersistedHistoryAsync(
        string userId,
        string threadId,
        string checkpoint,
        CancellationToken cancellationToken)
    {
        using var cosmosClient = new CosmosClient(cosmosConnectionString, GetCredential());
        var container = cosmosClient.GetContainer("agent-history", "agent-sessions");
        var response = await container.ReadItemAsync<PersistedSessionDocument>(
            threadId,
            new PartitionKey(userId),
            cancellationToken: cancellationToken);
        using var session = JsonDocument.Parse(response.Resource.SerializedSession);
        var messages = session.RootElement
            .GetProperty("stateBag")
            .GetProperty("InMemoryChatHistoryProvider")
            .GetProperty("messages")
            .EnumerateArray()
            .Select(message => new PersistedHistoryMessage(
                message.TryGetProperty("role", out var roleProperty) ? roleProperty.GetString() ?? "<none>" : "<none>",
                message.TryGetProperty("messageId", out var idProperty) ? idProperty.GetString() : null,
                message.TryGetProperty("contents", out var contents)
                    ? string.Join(" | ", contents.EnumerateArray().Select(content =>
                        content.TryGetProperty("text", out var textProperty) ? textProperty.GetString() : content.GetRawText()))
                    : "<no contents>"))
            .ToList();

        output.WriteLine(DescribePersistedHistory(checkpoint, messages));
        return messages;
    }

    private async Task<JsonDocument> ReadPersistedSessionAsync(
        string userId,
        string threadId,
        CancellationToken cancellationToken)
    {
        using var cosmosClient = new CosmosClient(cosmosConnectionString, GetCredential());
        var container = cosmosClient.GetContainer("agent-history", "agent-sessions");
        var response = await container.ReadItemAsync<PersistedSessionDocument>(
            threadId,
            new PartitionKey(userId),
            cancellationToken: cancellationToken);
        return JsonDocument.Parse(response.Resource.SerializedSession);
    }

    private async Task<List<FunctionResultContent>> RunLocationTurnAsync(
        ChatClientAgent agent,
        AGUIContinuationState continuation,
        string userId,
        string prompt,
        CancellationToken cancellationToken)
    {
        List<FunctionResultContent> toolResults = [];
        try
        {
            List<ChatMessage> userMessages = [CreateUserMessage(prompt)];
            // Use this session only while handling this user message and any client-tool requests
            // it triggers. Do not reuse it for a later message because it accumulates chat history.
            var session = await agent.CreateSessionAsync(cancellationToken);
            await foreach (var update in agent.RunStreamingAsync(
                userMessages,
                session,
                options: continuation.CreateRunOptions(),
                cancellationToken: cancellationToken))
            {
                continuation.Observe(update);
                toolResults.AddRange(update.Contents.OfType<FunctionResultContent>());
            }
        }
        catch (HttpRequestException exception)
        {
            using var diagnosticsHttp = CreateAgentHttpClient(userId);
            var serverFailure = await diagnosticsHttp.GetStringAsync("/_diagnostics/ag-ui-failure", cancellationToken);
            throw new InvalidOperationException(
                $"Location turn failed for prompt '{prompt}'. Server failure:{Environment.NewLine}{serverFailure}{Environment.NewLine}Agent diagnostics:{Environment.NewLine}{GetAgentDiagnostics()}",
                exception);
        }

        return toolResults;
    }

    private string GetAgentDiagnostics() =>
        string.Join(Environment.NewLine, agentDiagnostics.TakeLast(100));

    private static string DescribePersistedHistory(string checkpoint, IReadOnlyList<PersistedHistoryMessage> messages) =>
        $"Persisted session {checkpoint}:{Environment.NewLine}" +
        string.Join(Environment.NewLine, messages.Select((message, index) =>
            $"  [{index}] role={message.Role} id={message.MessageId ?? "<none>"} text={message.Text}"));

    private static bool ContainsPendingToolApprovalRequest(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("_pendingApprovalRequests") &&
                    property.Value.ValueKind == JsonValueKind.Array &&
                    property.Value.GetArrayLength() > 0)
                {
                    return true;
                }

                if (ContainsPendingToolApprovalRequest(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsPendingToolApprovalRequest(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsFoundryMemoryContext(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("text") &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString()?.Contains("## Memories", StringComparison.Ordinal) == true)
                {
                    return true;
                }

                if (property.NameEquals("sourceId") &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() == "Microsoft.Agents.AI.Foundry.FoundryMemoryProvider")
                {
                    return true;
                }

                if (ContainsFoundryMemoryContext(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsFoundryMemoryContext(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string DescribeRawPersistedHistory(IReadOnlyList<JsonElement> messages) =>
        string.Join(Environment.NewLine, messages.Select((message, index) =>
            $"  [{index}] {message.GetRawText()}"));

    internal static TokenCredential GetCredential() =>
         new ChainedTokenCredential(
            new VisualStudioCredential(),
            new VisualStudioCodeCredential(),
            new DefaultAzureCredential());
}