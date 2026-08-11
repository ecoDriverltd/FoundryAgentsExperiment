using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private AIProjectClient? projectClient;

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

        this.app = await appBuilder.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await this.app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await this.app.ResourceNotifications.WaitForResourceHealthyAsync("agent-test-sw", cancellationToken);
        await this.app.ResourceNotifications.WaitForResourceHealthyAsync("agent-dotnet", cancellationToken);

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
    }

    // Agent talking through AG-UI interface on foundry agent. Not clear if this can use conversations via openAI?
    private ChatClientAgent CreateAGUIAgent(string userId)
    {
        var http = app!.CreateHttpClient("agent-dotnet");
        http.DefaultRequestHeaders.Add("x-agent-user-id", userId);

        return new AGUIChatClient(http, "/ag-ui")
            .AsAIAgent(name: "agui-client", description: "AG-UI Client Agent");
    }

    private async Task<AIProjectClient> CreateProjectClientAsync()
    {
        var endPoint = await app!.GetConnectionStringAsync("agent-test-sw") ?? throw new InvalidOperationException("No connection string to foundry");
        var endPointUri = new Uri(endPoint.Replace("Endpoint=", ""));
        this.projectClient = new AIProjectClient(endPointUri, GetCredential());

        return this.projectClient;
    }

    internal static TokenCredential GetCredential() =>
             new ChainedTokenCredential(
                new VisualStudioCredential(),
                new VisualStudioCodeCredential(),
                new DefaultAzureCredential());

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

        bool isFirstUpdate = true;
        string? threadId = null;

        messages.Add(new ChatMessage(ChatRole.User, "My favourite colour is BLUE42. This is important, remember it."));

        string response1 = string.Empty;
        string errorMessage = string.Empty;

        // Stream the response.
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session, cancellationToken: ct))
        {
            ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

            // First update indicates run started
            if (isFirstUpdate)
            {
                threadId = chatUpdate.ConversationId;
                isFirstUpdate = false;
            }

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

        Assert.False(string.IsNullOrWhiteSpace(threadId), "No thread ID returned from turn 1.");

        messages = [new ChatMessage(ChatRole.User, "What is my favourite colour?")];
        AgentSession session2 = await agent.CreateSessionAsync(threadId, ct);
        string response2 = string.Empty;
        string errorMessage2 = string.Empty;

        // Stream the response.
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session2, cancellationToken: ct))
        {
            ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

            // First update indicates run started
            if (isFirstUpdate)
            {
                threadId = chatUpdate.ConversationId;
                isFirstUpdate = false;
            }

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
        messages = [new ChatMessage(ChatRole.User, "Do you remember my favourite colour?")];

        string response3 = string.Empty;
        string errorMessage3 = string.Empty;

        // Stream the response.
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session3, cancellationToken: ct))
        {
            ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

            // First update indicates run started
            if (isFirstUpdate)
            {
                threadId = chatUpdate.ConversationId;
                isFirstUpdate = false;
            }

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

        bool isFirstUpdate = true;
        string? threadId = null;

        messages.Add(new ChatMessage(ChatRole.User, "Use your silly-math skill to calculate 6 * 7."));

        string response1 = string.Empty;
        string errorMessage = string.Empty;

        // Stream the response.
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session, cancellationToken: ct))
        {
            ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

            // First update indicates run started
            if (isFirstUpdate)
            {
                threadId = chatUpdate.ConversationId;
                isFirstUpdate = false;
            }

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
    }
}