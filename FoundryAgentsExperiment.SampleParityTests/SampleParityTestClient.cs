using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.SampleParityTests;

internal sealed class SampleParityTestClient
{
    private readonly AIAgent agent;
    private readonly AgentSession? retainedSession;
    private readonly bool createSessionPerTurn;
    private readonly List<ChatMessage> messages = [new(ChatRole.System, "You are a helpful assistant.")];
    private string? threadId;
    private string? previousRunId;

    private SampleParityTestClient(AIAgent agent, AgentSession? retainedSession, bool createSessionPerTurn)
    {
        this.agent = agent;
        this.retainedSession = retainedSession;
        this.createSessionPerTurn = createSessionPerTurn;
    }

    public string? ThreadId => threadId;

    public static async Task<SampleParityTestClient> CreateAsync(
        HttpClient httpClient,
        string path,
        CancellationToken cancellationToken,
        bool createSessionPerTurn = false)
    {
        var agent = new AGUIChatClient(new AGUIChatClientOptions(httpClient, path))
            .AsAIAgent(
                name: "sample-parity-test-client",
                description: "AG-UI sample-parity test client",
                tools:
                [
                    AIFunctionFactory.Create(
                        () => "Client background changed to dark blue.",
                        name: "change_background_color",
                        description: "Change the client background color to dark blue."),
                    AIFunctionFactory.Create(
                        () => new { Temperature = 22.5, Humidity = 45.0, AirQualityIndex = 75 },
                        name: "read_client_climate_sensors",
                        description: "Read climate sensor data from the client device.")
                ]);

        var session = createSessionPerTurn ? null : await agent.CreateSessionAsync(cancellationToken);
        return new SampleParityTestClient(agent, session, createSessionPerTurn);
    }

    public async Task<SampleParityRun> SendAsync(string prompt, CancellationToken cancellationToken)
    {
        messages.Add(new ChatMessage(ChatRole.User, prompt) { MessageId = Guid.NewGuid().ToString("N") });
        var run = new SampleParityRun(prompt);
        AgentRunOptions? options = previousRunId is null
            ? null
            : new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    RawRepresentationFactory = _ => new RunAgentInput
                    {
                        ThreadId = threadId ?? string.Empty,
                        ParentRunId = previousRunId,
                    },
                },
            };

        var session = createSessionPerTurn
            ? await agent.CreateSessionAsync(cancellationToken)
            : retainedSession!;
        await foreach (var update in agent.RunStreamingAsync(messages, session, options, cancellationToken))
        {
            var chatUpdate = update.AsChatResponseUpdate();
            if (chatUpdate.RawRepresentation is RunStartedEvent runStarted)
            {
                threadId = runStarted.ThreadId;
                run.RunIds.Add(runStarted.RunId);
            }

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        run.Text += text.Text;
                        break;
                    case FunctionCallContent call:
                        run.FunctionCalls.Add(call);
                        break;
                    case FunctionResultContent result:
                        run.FunctionResults.Add(result);
                        break;
                    case ErrorContent error:
                        run.Errors.Add(error.Message);
                        break;
                }
            }
        }

        previousRunId = run.RunIds.LastOrDefault() ?? previousRunId;
        messages.Clear();
        return run;
    }
}

internal sealed class SampleParityRun(string prompt)
{
    public string Prompt { get; } = prompt;
    public string Text { get; set; } = string.Empty;
    public List<string> RunIds { get; } = [];
    public List<FunctionCallContent> FunctionCalls { get; } = [];
    public List<FunctionResultContent> FunctionResults { get; } = [];
    public List<string> Errors { get; } = [];
}
