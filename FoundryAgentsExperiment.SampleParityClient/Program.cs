using AGUI.Abstractions;
using AGUI.Client;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ILoggerFactory loggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

var serverUrl = Environment.GetEnvironmentVariable("SAMPLE_PARITY_AGENT_URL")
    ?? throw new InvalidOperationException("SAMPLE_PARITY_AGENT_URL is not set by Aspire.");

using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
var chatClient = new AGUIChatClient(new AGUIChatClientOptions(httpClient, serverUrl));
var agent = chatClient.AsAIAgent(
    name: "sample-parity-client",
    description: "AG-UI sample-parity terminal client",
    tools:
    [
        AIFunctionFactory.Create(
            () =>
            {
                Console.WriteLine("[Client tool] Changing the console background color to dark blue.");
                Console.BackgroundColor = ConsoleColor.DarkBlue;
                return "Console background changed to dark blue.";
            },
            name: "change_background_color",
            description: "Change the console background color to dark blue."),
        AIFunctionFactory.Create(
            () => new { Temperature = 22.5, Humidity = 45.0, AirQualityIndex = 75 },
            name: "read_client_climate_sensors",
            description: "Read climate sensor data from the client device.")
    ]);

var session = await agent.CreateSessionAsync();
var messages = new List<ChatMessage> { new(ChatRole.System, "You are a helpful assistant.") };
string? threadId = null;
string? previousRunId = null;

Console.WriteLine($"Connected to sample-parity AG-UI server at {serverUrl}");
Console.WriteLine("Try: 'What time is it?' or 'Change the background color.' Type ':q' to exit.");

while (true)
{
    Console.Write("\nUser (:q to exit): ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (input is ":q" or "quit")
    {
        break;
    }

    messages.Add(new ChatMessage(ChatRole.User, input) { MessageId = Guid.NewGuid().ToString("N") });
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

    string? currentRunId = null;
    await foreach (var update in agent.RunStreamingAsync(messages, session, options))
    {
        var chatUpdate = update.AsChatResponseUpdate();
        if (chatUpdate.RawRepresentation is RunStartedEvent runStarted)
        {
            threadId = runStarted.ThreadId;
            currentRunId = runStarted.RunId;
            Console.WriteLine($"[Run started] thread={threadId}, run={currentRunId}");
        }

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    Console.Write(text.Text);
                    break;
                case FunctionCallContent call:
                    Console.WriteLine($"\n[Function call] {call.Name} ({call.CallId})");
                    break;
                case FunctionResultContent result:
                    Console.WriteLine($"\n[Function result] {result.CallId}: {result.Result}");
                    break;
                case ErrorContent error:
                    Console.WriteLine($"\n[Error] {error.Message}");
                    break;
            }
        }
    }

    previousRunId = currentRunId;
    messages.Clear();
    Console.WriteLine();
}
