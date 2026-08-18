using System.Text.Json;

namespace FoundryAgentsExperiment.Agent;

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

                    logger.LogInformation(
                        "[Wire] POST /ag-ui threadId={ThreadId} runId={RunId} parentRunId={ParentRunId} messageCount={MessageCount} userId={UserId}",
                        threadId,
                        runId,
                        parentRunId,
                        messageCount,
                        context.Request.Headers["x-agent-user-id"].ToString());
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "[Wire] POST /ag-ui - failed to parse body for logging");
                }
            }

            await next(context);
        });
}
