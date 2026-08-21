using System.Text.Json;

namespace FoundryAgentsExperiment.Agent.AgentServices;

/// <summary>
/// Only to be used to debugging and troubleshooting, should not be used in production or for any other purpose.
/// </summary>
public static class AGUIRequestLoggingMiddleware
{
    public static IApplicationBuilder UseAGUIRequestLogging(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/ag-ui"))
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;

                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AGUIRequestLogging");

                try
                {
                    using var document = JsonDocument.Parse(body);
                    var threadId = document.RootElement.TryGetProperty("threadId", out var thread) ? thread.GetString() : null;
                    var runId = document.RootElement.TryGetProperty("runId", out var run) ? run.GetString() : null;
                    var parentRunId = document.RootElement.TryGetProperty("parentRunId", out var parentRun) ? parentRun.GetString() : null;
                    var messageCount = document.RootElement.TryGetProperty("messages", out var messages) ? messages.GetArrayLength() : -1;
                    var messageSummary = messages.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", messages.EnumerateArray().Select(DescribeMessage))
                        : "<none>";

                    logger.LogInformation(
                        "[Wire] POST /ag-ui threadId={ThreadId} runId={RunId} parentRunId={ParentRunId} messageCount={MessageCount} messages={Messages} userId={UserId}",
                        threadId,
                        runId,
                        parentRunId,
                        messageCount,
                        messageSummary,
                        context.Request.Headers["x-agent-user-id"].ToString());
                    context.RequestServices.GetRequiredService<AgUiFailureDiagnostics>().RecordRequest(
                        context.Request.Headers["x-agent-user-id"].ToString(),
                        $"threadId={threadId}; runId={runId}; parentRunId={parentRunId}; messageCount={messageCount}; messages={messageSummary}");
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "[Wire] POST /ag-ui - failed to parse body for logging");
                }
            }

            try
            {
                await next(context);
            }
            catch (Exception exception)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("AGUIRequestLogging");
                logger.LogError(
                    exception,
                    "[Wire] POST /ag-ui failed path={Path} userId={UserId}",
                    context.Request.Path,
                    context.Request.Headers["x-agent-user-id"].ToString());
                context.RequestServices.GetRequiredService<AgUiFailureDiagnostics>().Record(
                    context.Request.Headers["x-agent-user-id"].ToString(),
                    exception);
                throw;
            }
        });

    private static string DescribeMessage(JsonElement message)
    {
        var id = message.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        var role = message.TryGetProperty("role", out var roleProperty) ? roleProperty.GetString() : null;
        var toolCallId = message.TryGetProperty("toolCallId", out var toolCallIdProperty) ? toolCallIdProperty.GetString() : null;
        var contentKind = message.TryGetProperty("content", out var content)
            ? content.ValueKind.ToString()
            : "<none>";
        var contentSummary = content.ValueKind == JsonValueKind.String
            ? content.GetString() is { } text
                ? text.Length <= 80 ? text : $"{text[..80]}…"
                : "<null>"
            : null;

        var toolCalls = message.TryGetProperty("toolCalls", out var toolCallsProperty) && toolCallsProperty.ValueKind == JsonValueKind.Array
            ? string.Join("|", toolCallsProperty.EnumerateArray().Select(DescribeToolCall))
            : null;

        return $"{role ?? "<none>"}:{id ?? "<null>"}:toolCallId={toolCallId ?? "<null>"}:{contentKind}{(contentSummary is null ? string.Empty : $"={contentSummary}")}{(toolCalls is null ? string.Empty : $":toolCalls={toolCalls}")}";
    }

    private static string DescribeToolCall(JsonElement toolCall)
    {
        var id = toolCall.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        var name = toolCall.TryGetProperty("function", out var functionProperty) &&
            functionProperty.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString()
                : null;
        return $"{name ?? "<unknown>"}:{id ?? "<null>"}";
    }
}
