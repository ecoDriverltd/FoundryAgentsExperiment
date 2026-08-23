using AGUI.Client;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.AI.Projects;
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
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private string? previousRequirePerServiceCallChatHistoryPersistence;

    private const string CompactionTriggerTokensEnvironmentVariable = "SessionPersistence__CompactionTriggerTokens";
    private const string CompactionMinimumPreservedGroupsEnvironmentVariable = "SessionPersistence__CompactionMinimumPreservedGroups";
    private const string EnableFoundryMemoryEnvironmentVariable = "EnableFoundryMemory";
    private const string RequirePerServiceCallChatHistoryPersistenceEnvironmentVariable = "SessionPersistence__RequirePerServiceCallChatHistoryPersistence";
    private const string TestUserIdPrefix = "integration-test-";

    // Aspire's Cosmos resource re-runs its ARM deployment (Bicep) on every AppHost start even when the
    // underlying account already exists, to reconcile any container/config changes (e.g. the
    // "agent-sessions" container added for CosmosAgentSessionStore) - this reconciliation deployment
    // routinely exceeds the standard per-turn timeout on its own, well before the agent-dotnet/agent-test-sw
    // resources even start. Give BuildAsync/StartAsync a longer budget.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);

    private sealed record CreatedConversation(HttpClient Http, string UserId, string ThreadId, string Prompt);
    private sealed record PersistedSessionId(string Id);
    private sealed record PersistedSessionDocument(string SerializedSession);
    private sealed record PersistedChatHistoryDocument(string Message, long Timestamp);
    private sealed record PersistedHistoryMessage(string Role, string? MessageId, string Text);
    private sealed record ModelRequestSnapshot(string ConversationId, int MessageCount, bool ContainsMemoryContext, string[] MemoryContextTexts);

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
        this.previousRequirePerServiceCallChatHistoryPersistence = Environment.GetEnvironmentVariable(RequirePerServiceCallChatHistoryPersistenceEnvironmentVariable);
        Environment.SetEnvironmentVariable(CompactionTriggerTokensEnvironmentVariable, "512");
        Environment.SetEnvironmentVariable(CompactionMinimumPreservedGroupsEnvironmentVariable, "2");
        Environment.SetEnvironmentVariable(EnableFoundryMemoryEnvironmentVariable, "true");
        if (this.previousRequirePerServiceCallChatHistoryPersistence is null)
        {
            Environment.SetEnvironmentVariable(RequirePerServiceCallChatHistoryPersistenceEnvironmentVariable, "true");
        }

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

        await DeleteTestSessionsAsync(TestContext.Current.CancellationToken);

        if (app is not null)
        {
            await app.DisposeAsync();
        }

        Environment.SetEnvironmentVariable(CompactionTriggerTokensEnvironmentVariable, this.previousCompactionTriggerTokens);
        Environment.SetEnvironmentVariable(CompactionMinimumPreservedGroupsEnvironmentVariable, this.previousCompactionMinimumPreservedGroups);
        Environment.SetEnvironmentVariable(EnableFoundryMemoryEnvironmentVariable, this.previousEnableFoundryMemory);
        Environment.SetEnvironmentVariable(RequirePerServiceCallChatHistoryPersistenceEnvironmentVariable, this.previousRequirePerServiceCallChatHistoryPersistence);

    }

    [Fact]
    public async Task AGUIHistoryDiagnosticsCaptureTwoTurnSkillConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = TestUserIdPrefix + Guid.NewGuid().ToString("N");
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

        var skillPromptMessageId = "client-" + Guid.NewGuid().ToString("N");
        List<ChatMessage> secondTurnMessages =
        [
            // Skill returns 42 regardless of question, so we know it's working if it returns 42 for the 1 + 1 question.
            new(ChatRole.User, "Use your silly-math skill to calculate 1 + 1.")
            {
                MessageId = skillPromptMessageId,
            },
        ];
        var skillResponse = new StringBuilder();
        var secondTurnSession = await agent.CreateSessionAsync(ct);
        await foreach (var update in agent.RunStreamingAsync(
            secondTurnMessages,
            secondTurnSession,
            options: continuation.CreateRunOptions(),
            cancellationToken: ct))
        {
            continuation.Observe(update);
            skillResponse.Append(string.Concat(update.Contents.OfType<TextContent>().Select(content => content.Text)));
        }

        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId));

        using var http = CreateAgentHttpClient(userId);
        var conversation = await http.GetFromJsonAsync<ConversationDetail>(
            $"/conversations/{Uri.EscapeDataString(continuation.ThreadId!)}",
            ct);

        Assert.NotNull(conversation);
        Assert.Contains(conversation.Messages, message => message.Text.Contains("Good evening", StringComparison.Ordinal));
        Assert.Contains(conversation.Messages, message => message.Text.Contains("1 + 1", StringComparison.Ordinal));
        Assert.Contains("42", skillResponse.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(continuation.PreviousRunId));
        Assert.DoesNotContain(conversation.Messages, message => message.Role == ChatRole.Assistant &&
            string.IsNullOrWhiteSpace(message.Text) &&
            !message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent));
        Assert.Equal(1, conversation.Messages.Count(message => message.Text == "Good evening."));
        var skillPromptMessages = conversation.Messages
            .Where(message => message.Role == ChatRole.User && message.Text == secondTurnMessages[0].Text)
            .ToList();
        Assert.Equal(skillPromptMessageId, Assert.Single(skillPromptMessages).MessageId);
        Assert.Contains(conversation.Messages, message =>
            message.Role == ChatRole.Assistant &&
            message.Contents.Any(content => content is FunctionCallContent));
        Assert.Contains(conversation.Messages, message =>
            message.Role == ChatRole.Tool &&
            message.Contents.Any(content => content is FunctionResultContent));

        AssertPersistedTranscript(
            await ReadPersistedChatHistoryAsync(continuation.ThreadId!, ct),
            ["Good evening.", secondTurnMessages[0].Text!],
            expectedFunctionCallCount: 1,
            expectedFunctionResultCount: 1);
    }

    [Fact]
    public async Task AGUIServerToolSecondUserTurnPersistsInSessionHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = TestUserIdPrefix + Guid.NewGuid().ToString("N");
        var agent = CreateAGUIAgent(userId);
        var continuation = new AGUIContinuationState();
        const string firstPrompt = "Hello.";
        const string secondPrompt = "What time is it? Use the get_current_time tool.";

        await RunTurnAsync(agent, continuation, userId, firstPrompt, ct);
        await RunTurnAsync(agent, continuation, userId, secondPrompt, ct);

        Assert.False(string.IsNullOrWhiteSpace(continuation.ThreadId));

        var messages = await ReadPersistedChatHistoryAsync(continuation.ThreadId!, ct);
        var persistedSecondPromptCount = messages.Count(message =>
            message.GetProperty("role").GetString() == "user" &&
            message.GetProperty("contents").EnumerateArray().Any(content =>
                content.TryGetProperty("text", out var text) && text.GetString() == secondPrompt));

        Assert.True(
            persistedSecondPromptCount == 1,
            $"Expected the second user prompt once but found {persistedSecondPromptCount}.{Environment.NewLine}" +
            DescribeRawPersistedHistory(messages));
        Assert.Contains(
            messages,
            message => message.GetProperty("contents").EnumerateArray().Any(content =>
                content.TryGetProperty("$type", out var type) && type.GetString() == "functionCall"));
        Assert.Contains(
            messages,
            message => message.GetProperty("contents").EnumerateArray().Any(content =>
                content.TryGetProperty("$type", out var type) && type.GetString() == "functionResult"));
        AssertPersistedTranscript(
            messages,
            [firstPrompt, secondPrompt],
            expectedFunctionCallCount: 1,
            expectedFunctionResultCount: 1);
    }

    [Fact]
    public async Task AGUICompactionPersistsStateAndPreservesRecentHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = TestUserIdPrefix + Guid.NewGuid().ToString("N");
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
        AssertPersistedTranscript(
            await ReadPersistedChatHistoryAsync(continuation.ThreadId!, ct),
            ["The most recent meeting room is Cedar-204. Remember this exactly.", "What is the archive access phrase and what is the most recent meeting room?"]);
    }

    [Fact]
    public async Task AGUIClientToolContinuationsPersistCompleteSessionHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = TestUserIdPrefix + Guid.NewGuid().ToString("N");
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

        using var persistedSession = await ReadPersistedSessionAsync(userId, continuation.ThreadId!, ct);
        var stateBag = persistedSession.RootElement.GetProperty("stateBag");
        var persistedMessages = await ReadPersistedChatHistoryAsync(continuation.ThreadId!, ct);
        foreach (var prompt in new[] { "Where am I?", "Where am I now?" })
        {
            var promptCount = persistedMessages.Count(message =>
                message.GetProperty("role").GetString() == "user" &&
                message.GetProperty("contents").EnumerateArray().Any(content =>
                    content.TryGetProperty("text", out var text) && text.GetString() == prompt));
            Assert.True(
                promptCount == 1,
                $"Expected client prompt '{prompt}' once but found {promptCount}.{Environment.NewLine}" +
                DescribeRawPersistedHistory(persistedMessages));
        }

        var rawFunctionCallIds = persistedMessages
            .SelectMany(message => message.GetProperty("contents").EnumerateArray())
            .Where(content => content.TryGetProperty("$type", out var type) && type.GetString() == "functionCall")
            .Select(content => content.GetProperty("callId").GetString())
            .Where(callId => !string.IsNullOrWhiteSpace(callId))
            .Select(callId => callId!)
            .ToList();
        var rawFunctionResultIds = persistedMessages
            .SelectMany(message => message.GetProperty("contents").EnumerateArray())
            .Where(content => content.TryGetProperty("$type", out var type) && type.GetString() == "functionResult")
            .Select(content => content.GetProperty("callId").GetString())
            .Where(callId => !string.IsNullOrWhiteSpace(callId))
            .Select(callId => callId!)
            .ToList();
        Assert.Equal(rawFunctionCallIds.Order(), rawFunctionResultIds.Order());
        Assert.Equal(2, rawFunctionCallIds.Count);
        Assert.Equal(2, rawFunctionCallIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, rawFunctionResultIds.Count);
        Assert.Equal(2, rawFunctionResultIds.Distinct(StringComparer.Ordinal).Count());
        Assert.False(ContainsPendingToolApprovalRequest(stateBag), DescribeRawPersistedHistory(persistedMessages));
        AssertPersistedTranscript(
            persistedMessages,
            ["Where am I?", "Where am I now?"],
            expectedFunctionCallCount: 2,
            expectedFunctionResultCount: 2);
    }


    [Fact]
    public async Task ConversationEndpointsPersistTranscriptAndEnforceOwnership()
    {
        var created = await CreateConversationAsync(
            TestUserIdPrefix + Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
        using var http = created.Http;

        var conversations = await http.GetFromJsonAsync<List<ConversationSummary>>(
            "/conversations",
            TestContext.Current.CancellationToken);
        var conversation = Assert.Single(conversations!, item => item.Id == created.ThreadId);

        Assert.StartsWith("Conversation endpoint integration test", conversation.Title, StringComparison.Ordinal);

        var transcript = await http.GetFromJsonAsync<ConversationDetail>(
            $"/conversations/{Uri.EscapeDataString(created.ThreadId)}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(transcript);
        Assert.Equal(created.ThreadId, transcript.ConversationId);
        Assert.Contains(transcript.Messages,
            message => message.Role == ChatRole.User &&
                       message.Text?.Contains(created.Prompt, StringComparison.Ordinal) == true);

        AssertPersistedTranscript(
            await ReadPersistedChatHistoryAsync(created.ThreadId, TestContext.Current.CancellationToken),
            [created.Prompt]);

        using var otherUserHttp = CreateAgentHttpClient(TestUserIdPrefix + Guid.NewGuid().ToString("N"));
        using var otherUserResponse = await otherUserHttp.GetAsync(
            $"/conversations/{Uri.EscapeDataString(created.ThreadId)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, otherUserResponse.StatusCode);
    }

    [Fact]
    public async Task AGUIAgentRecallsFactsAcrossSessionsAndViaMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = TestUserIdPrefix + Guid.NewGuid().ToString("N");
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
        using var persistedSession = await ReadPersistedSessionAsync(userId, continuation.ThreadId!, ct);
        var stateBag = persistedSession.RootElement.GetProperty("stateBag");
        Assert.False(
            ContainsFoundryMemoryContext(await ReadPersistedChatHistoryAsync(continuation.ThreadId!, ct)),
            $"Foundry memory search context was persisted in chat history.{Environment.NewLine}{stateBag.GetRawText()}");

        Assert.Contains("BLUE42", response2, StringComparison.OrdinalIgnoreCase);

        var projectConnectionString = await app!.GetConnectionStringAsync("agent-test-sw", TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("No Foundry project connection string is available.");
        var projectConnection = new DbConnectionStringBuilder { ConnectionString = projectConnectionString };
        var projectEndpoint = projectConnection.TryGetValue("Endpoint", out var endpoint) && endpoint is string value
            ? value
            : throw new InvalidOperationException("The Foundry project connection string has no Endpoint value.");
        var projectClient = new AIProjectClient(new Uri(projectEndpoint), GetCredential());
        var foundMemory = false;
        const int retryLimit = 5;
        const int retryDelayMs = 1000;

        for (var retryCount = 0; retryCount < retryLimit; retryCount++)
        {
            var memories = await projectClient.MemoryStores
                .GetMemoriesAsync("agent-dotnet-memory", userId, cancellationToken: ct)
                .ToListAsync(cancellationToken: ct);
            var relevantMemory = memories.FirstOrDefault(memory =>
                memory.Content.Contains("BLUE42", StringComparison.OrdinalIgnoreCase));
            if (relevantMemory is not null)
            {
                foundMemory = true;
                output.WriteLine($"Found relevant memory: {relevantMemory.Content}");
                break;
            }

            await Task.Delay(retryDelayMs, ct);
        }

        // This is testing for something from the memory store in an isolated session
        // (not resuming the previous conversation).
        Assert.True(foundMemory, "Memory not found in memory store after five seconds.");

        var thirdTurnSession = await agent.CreateSessionAsync(ct);
        messages = [CreateUserMessage("Do you remember my favourite colour?")];
        var response3 = string.Empty;

        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            messages,
            thirdTurnSession,
            cancellationToken: ct))
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    response3 += textContent.Text;
                }
            }
        }

        Assert.Contains("BLUE42", response3, StringComparison.OrdinalIgnoreCase);
        AssertPersistedTranscript(
            await ReadPersistedChatHistoryAsync(continuation.ThreadId!, ct),
            [
                "My favourite colour is BLUE42. This is important, remember it.",
                "What is my favourite colour?",
            ]);
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
        if (!userId.StartsWith(TestUserIdPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Only generated integration test user IDs may be cleaned up.");

        testUserIds.Add(userId);
    }

    private async Task DeleteTestSessionsAsync(CancellationToken cancellationToken)
    {
        if (testUserIds.Count == 0 || cosmosConnectionString is null)
            return;

        using var cosmosClient = new CosmosClient(cosmosConnectionString, GetCredential());
        var sessionContainer = cosmosClient.GetContainer("agent-history", "agent-sessions");
        var historyContainer = cosmosClient.GetContainer("agent-history", "agent-chat-history");

        foreach (var userId in testUserIds)
        {
            var query = new QueryDefinition("SELECT c.id FROM c WHERE c.userId = @userId")
                .WithParameter("@userId", userId);
            using var iterator = sessionContainer.GetItemQueryIterator<PersistedSessionId>(query);

            var sessionIds = new List<string>();
            while (iterator.HasMoreResults)
            {
                foreach (var session in await iterator.ReadNextAsync(cancellationToken))
                {
                    sessionIds.Add(session.Id);
                    var historyQuery = new QueryDefinition("SELECT c.id FROM c WHERE c.conversationId = @conversationId")
                        .WithParameter("@conversationId", session.Id);
                    using var historyIterator = historyContainer.GetItemQueryIterator<PersistedSessionId>(
                        historyQuery,
                        requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(session.Id) });
                    while (historyIterator.HasMoreResults)
                    {
                        foreach (var historyItem in await historyIterator.ReadNextAsync(cancellationToken))
                        {
                            await historyContainer.DeleteItemAsync<PersistedSessionId>(
                                historyItem.Id,
                                new PartitionKey(session.Id),
                                cancellationToken: cancellationToken);
                        }
                    }

                    await sessionContainer.DeleteItemAsync<PersistedSessionId>(
                        session.Id,
                        new PartitionKey(userId),
                        cancellationToken: cancellationToken);
                }
            }

            foreach (var sessionId in sessionIds)
            {
                using var historyIterator = historyContainer.GetItemQueryIterator<PersistedSessionId>(
                    new QueryDefinition("SELECT c.id FROM c WHERE c.conversationId = @conversationId")
                        .WithParameter("@conversationId", sessionId),
                    requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(sessionId) });
                if (historyIterator.HasMoreResults && (await historyIterator.ReadNextAsync(cancellationToken)).Any())
                {
                    throw new InvalidOperationException($"Integration test cleanup left history records for session '{sessionId}'.");
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
                return new CreatedConversation(http, userId, threadId, prompt);
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

    private static void AssertPersistedTranscript(
        IReadOnlyList<JsonElement> messages,
        IEnumerable<string> expectedUserPrompts,
        int? expectedFunctionCallCount = null,
        int? expectedFunctionResultCount = null)
    {
        var rawTranscript = DescribeRawPersistedHistory(messages);
        var expectedPrompts = expectedUserPrompts.ToArray();

        foreach (var prompt in expectedPrompts)
        {
            var promptCount = messages.Count(message =>
                message.GetProperty("role").GetString() == "user" &&
                GetTextContents(message).Contains(prompt, StringComparer.Ordinal));
            Assert.True(
                promptCount == 1,
                $"Expected user prompt '{prompt}' once but found {promptCount}.{Environment.NewLine}{rawTranscript}");
        }

        var userMessages = messages
            .Where(message => message.GetProperty("role").GetString() == "user")
            .ToList();
        Assert.All(
            userMessages,
            message => Assert.False(string.IsNullOrWhiteSpace(GetMessageId(message)), $"Persisted user message has no message ID.{Environment.NewLine}{rawTranscript}"));
        AssertUnique(
            userMessages.Select(GetMessageId).Where(id => id is not null)!,
            "user message IDs",
            rawTranscript);

        var assistantMessages = messages
            .Where(message => message.GetProperty("role").GetString() == "assistant")
            .ToList();
        Assert.All(
            assistantMessages,
            message => Assert.True(
                GetContents(message).Any(),
                $"Persisted assistant message has no content.{Environment.NewLine}{rawTranscript}"));
        AssertUnique(
            assistantMessages.Select(GetMessageId).Where(id => !string.IsNullOrWhiteSpace(id))!,
            "assistant message IDs",
            rawTranscript);

        var functionCallIds = GetProtocolCallIds(messages, "functionCall");
        var functionResultIds = GetProtocolCallIds(messages, "functionResult");
        AssertUnique(functionCallIds, "function call IDs", rawTranscript);
        AssertUnique(functionResultIds, "function result IDs", rawTranscript);

        if (expectedFunctionCallCount is not null)
        {
            Assert.Equal(expectedFunctionCallCount.Value, functionCallIds.Count);
        }

        if (expectedFunctionResultCount is not null)
        {
            Assert.Equal(expectedFunctionResultCount.Value, functionResultIds.Count);
        }

        if (functionCallIds.Count > 0 || functionResultIds.Count > 0)
        {
            Assert.Equal(functionCallIds.Order(), functionResultIds.Order());
        }
    }

    private static IEnumerable<string> GetTextContents(JsonElement message) =>
        GetContents(message)
            .Where(content => content.TryGetProperty("text", out _))
            .Select(content => content.GetProperty("text").GetString())
            .Where(text => text is not null)!;

    private static IEnumerable<JsonElement> GetContents(JsonElement message) =>
        message.TryGetProperty("contents", out var contents)
            ? contents.EnumerateArray()
            : [];

    private static string? GetMessageId(JsonElement message) =>
        message.TryGetProperty("messageId", out var messageId)
            ? messageId.GetString()
            : null;

    private static List<string> GetProtocolCallIds(IReadOnlyList<JsonElement> messages, string contentType) =>
        messages
            .SelectMany(GetContents)
            .Where(content => content.TryGetProperty("$type", out var type) && type.GetString() == contentType)
            .Select(content => content.GetProperty("callId").GetString())
            .Where(callId => !string.IsNullOrWhiteSpace(callId))
            .Select(callId => callId!)
            .ToList();

    private static void AssertUnique(IEnumerable<string> identities, string identityName, string rawTranscript)
    {
        var values = identities.ToList();
        Assert.True(
            values.Count == values.Distinct(StringComparer.Ordinal).Count(),
            $"Persisted {identityName} are duplicated.{Environment.NewLine}{rawTranscript}");
    }

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
        var messages = (await ReadPersistedChatHistoryAsync(threadId, cancellationToken))
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

    private async Task<List<JsonElement>> ReadPersistedChatHistoryAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        using var cosmosClient = new CosmosClient(cosmosConnectionString, GetCredential());
        var container = cosmosClient.GetContainer("agent-history", "agent-chat-history");
        var query = new QueryDefinition(
            "SELECT c.message, c.timestamp FROM c WHERE c.conversationId = @conversationId AND c.type = 'ChatMessage' ORDER BY c.timestamp")
            .WithParameter("@conversationId", threadId);
        using var iterator = container.GetItemQueryIterator<PersistedChatHistoryDocument>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(threadId) });
        var messages = new List<JsonElement>();

        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync(cancellationToken))
            {
                var message = JsonNode.Parse(document.Message)
                    ?? throw new InvalidOperationException("Cosmos chat history contains an empty message.");
                NormalizeJsonPropertyNames(message);
                using var normalizedMessage = JsonDocument.Parse(message.ToJsonString());
                messages.Add(normalizedMessage.RootElement.Clone());
            }
        }

        return messages;
    }

    private static void NormalizeJsonPropertyNames(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                if (property.Value is not null)
                {
                    NormalizeJsonPropertyNames(property.Value);
                }

                if (!property.Key.StartsWith('$') && property.Key.Length > 0)
                {
                    var normalizedName = char.ToLowerInvariant(property.Key[0]) + property.Key[1..];
                    if (normalizedName != property.Key)
                    {
                        jsonObject.Remove(property.Key);
                        jsonObject[normalizedName] = property.Value;
                    }
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray.Where(item => item is not null))
            {
                NormalizeJsonPropertyNames(item!);
            }
        }
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

    private static bool ContainsFoundryMemoryContext(IEnumerable<JsonElement> messages) =>
        messages.Any(ContainsFoundryMemoryContext);

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

    private static JsonElement[] GetInMemoryHistoryMessages(JsonElement session) =>
        session
            .GetProperty("stateBag")
            .GetProperty("InMemoryChatHistoryProvider")
            .GetProperty("messages")
            .EnumerateArray()
            .ToArray();

    internal static TokenCredential GetCredential() =>
         new ChainedTokenCredential(
            new VisualStudioCredential(),
            new VisualStudioCodeCredential(),
            new DefaultAzureCredential());
}