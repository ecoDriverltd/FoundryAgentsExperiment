using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.AI.Projects;
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
public class AgentChatTests : IAsyncLifetime
{
    private DistributedApplication? app;

    private readonly ITestOutputHelper output;

    private CancellationTokenSource? resourceLogCts;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public AgentChatTests(ITestOutputHelper output) => this.output = output;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
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
            clientBuilder.AddStandardResilienceHandler();
        });

        this.app = await appBuilder.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await this.app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
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

    [Fact]
    public async Task AGUIAgentRecallsFact1()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = CreateAGUIAgent("test-" + Guid.NewGuid().ToString("N"));

        AgentSession session = await agent.CreateSessionAsync(ct);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are a helpful assistant.")
        ];

        bool isFirstUpdate = true;
        string? threadId = null;

        messages.Add(new ChatMessage(ChatRole.User, "My favourite colour is BLUE42."));

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

    }
}