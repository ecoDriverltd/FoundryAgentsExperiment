using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Agent.AgentServices;

/// <summary>
/// Records the final, already-composed requests sent to the model without logging user or tool payloads.
/// </summary>
internal sealed class ModelRequestLoggingChatClient(IChatClient innerClient, ILogger logger) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        LogStart("non-streaming", request);

        var response = await base.GetResponseAsync(request, options, cancellationToken);
        LogFunctionCalls("non-streaming", response.Messages.SelectMany(message => message.Contents));
        LogCompleted("non-streaming");
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        LogStart("streaming", request);

        await foreach (var update in base.GetStreamingResponseAsync(request, options, cancellationToken))
        {
            LogFunctionCalls("streaming", update.Contents);

            yield return update;
        }

        LogCompleted("streaming");
    }

    private void LogStart(string mode, IReadOnlyCollection<ChatMessage> messages) =>
        Log(
            "[Model] Start mode={Mode} traceId={TraceId} messageCount={MessageCount} messages={Messages}",
            mode,
            System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "<none>",
            messages.Count,
            DescribeMessages(messages));

    private void LogFunctionCalls(string mode, IEnumerable<AIContent> contents)
    {
        var functionCalls = contents.OfType<FunctionCallContent>()
            .Select(call => $"{call.Name}:{call.CallId}")
            .ToArray();
        if (functionCalls.Length > 0)
        {
            Log(
                "[Model] Function calls mode={Mode} traceId={TraceId} calls={FunctionCalls}",
                mode,
                System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "<none>",
                string.Join(",", functionCalls));
        }
    }

    private void LogCompleted(string mode) =>
        Log(
            "[Model] Completed mode={Mode} traceId={TraceId}",
            mode,
            System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "<none>");

    private void Log(string message, params object?[] arguments)
    {
        logger.LogInformation(message, arguments);
        Console.WriteLine($"{message} | {string.Join(", ", arguments.Select(argument => argument?.ToString() ?? "<null>"))}");
    }

    private static string DescribeMessages(IEnumerable<ChatMessage> messages) =>
        string.Join(", ", messages.Select(message =>
        {
            var calls = message.Contents.OfType<FunctionCallContent>().Select(call => $"call:{call.Name}:{call.CallId}");
            var results = message.Contents.OfType<FunctionResultContent>().Select(result => $"result:{result.CallId}");
            var identifiers = string.Join("|", calls.Concat(results));
            return $"{message.Role}:messageId={message.MessageId ?? "<none>"}" +
                (string.IsNullOrEmpty(identifiers) ? string.Empty : $":{identifiers}");
        }));
}