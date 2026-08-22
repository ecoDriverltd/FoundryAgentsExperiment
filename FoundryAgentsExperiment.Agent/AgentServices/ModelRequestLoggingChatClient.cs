using Microsoft.Extensions.AI;
using System.Diagnostics;

namespace FoundryAgentsExperiment.Agent.AgentServices;

/// <summary>
/// Records the final, already-composed requests sent to the model without logging user or tool payloads.
/// </summary>
internal sealed class ModelRequestLoggingChatClient(
    IChatClient innerClient,
    ILogger logger) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        var stopwatch = Stopwatch.StartNew();
        LogStart("non-streaming", request, options);
        LogUnmatchedFunctionCalls("non-streaming", request);

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(request, options, cancellationToken);
        }
        catch (Exception exception)
        {
            throw CreateRequestException("non-streaming", request, options, exception);
        }

        LogFunctionCalls("non-streaming", response.Messages.SelectMany(message => message.Contents));
        LogCompleted("non-streaming", stopwatch.ElapsedMilliseconds);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        var stopwatch = Stopwatch.StartNew();
        var receivedFirstUpdate = false;
        var lastUpdateAt = TimeSpan.Zero;
        LogStart("streaming", request, options);
        LogUnmatchedFunctionCalls("streaming", request);

        await using var enumerator = base.GetStreamingResponseAsync(request, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            if (!await MoveNextAsync(enumerator, request, options))
            {
                break;
            }

            var update = enumerator.Current;
            if (!receivedFirstUpdate)
            {
                receivedFirstUpdate = true;
                Log("[Timing] Model first update traceId={TraceId} elapsedMs={ElapsedMs}",
                    Activity.Current?.TraceId.ToString() ?? "<none>", stopwatch.ElapsedMilliseconds);
            }

            lastUpdateAt = stopwatch.Elapsed;
            LogFunctionCalls("streaming", update.Contents);
            yield return update;
        }

        Log("[Timing] Model stream ended traceId={TraceId} elapsedMs={ElapsedMs} afterLastUpdateMs={AfterLastUpdateMs}",
            Activity.Current?.TraceId.ToString() ?? "<none>",
            stopwatch.ElapsedMilliseconds,
            receivedFirstUpdate ? (stopwatch.Elapsed - lastUpdateAt).TotalMilliseconds : 0);
        LogCompleted("streaming", stopwatch.ElapsedMilliseconds);
    }

    private void LogStart(string mode, IReadOnlyCollection<ChatMessage> messages, ChatOptions? options)
    {
        Log(
            "[Model] Start mode={Mode} traceId={TraceId} conversationId={ConversationId} messageCount={MessageCount} messages={Messages}",
            mode,
            System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "<none>",
            options?.ConversationId ?? "<none>",
            messages.Count,
            DescribeMessages(messages));
    }

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

    private void LogUnmatchedFunctionCalls(string mode, IReadOnlyCollection<ChatMessage> messages)
    {
        var functionCallIds = messages
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);
        var functionResultIds = messages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(result => result.CallId)
            .ToHashSet(StringComparer.Ordinal);
        var unmatchedCallIds = functionCallIds.Except(functionResultIds, StringComparer.Ordinal).ToArray();

        if (unmatchedCallIds.Length > 0)
        {
            Log(
                "[Model] Unmatched function calls mode={Mode} traceId={TraceId} callIds={CallIds}",
                mode,
                System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "<none>",
                string.Join(",", unmatchedCallIds));
        }
    }

    private void LogCompleted(string mode, long elapsedMilliseconds) =>
        Log(
            "[Model] Completed mode={Mode} traceId={TraceId} elapsedMs={ElapsedMs}",
            mode,
            Activity.Current?.TraceId.ToString() ?? "<none>",
            elapsedMilliseconds);

    private void Log(string message, params object?[] arguments)
    {
        logger.LogInformation(message, arguments);
        Console.WriteLine($"{message} | {string.Join(", ", arguments.Select(argument => argument?.ToString() ?? "<null>"))}");
    }

    private static InvalidOperationException CreateRequestException(
        string mode,
        IReadOnlyCollection<ChatMessage> messages,
        ChatOptions? options,
        Exception exception) =>
        new(
            $"Model {mode} request failed. ConversationId={options?.ConversationId ?? "<none>"}; " +
            $"messages={DescribeMessages(messages)}",
            exception);

    private static async ValueTask<bool> MoveNextAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator,
        IReadOnlyCollection<ChatMessage> messages,
        ChatOptions? options)
    {
        try
        {
            return await enumerator.MoveNextAsync();
        }
        catch (Exception exception)
        {
            throw CreateRequestException("streaming", messages, options, exception);
        }
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