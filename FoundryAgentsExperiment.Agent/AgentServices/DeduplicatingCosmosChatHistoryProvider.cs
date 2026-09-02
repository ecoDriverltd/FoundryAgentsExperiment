using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Diagnostics;

namespace FoundryAgentsExperiment.Agent.AgentServices;

public sealed class DeduplicatingCosmosChatHistoryProvider(
    CosmosChatHistoryProvider inner,
    ILogger<DeduplicatingCosmosChatHistoryProvider> logger) : ChatHistoryProvider
{
    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default) =>
        inner.InvokingAsync(context, cancellationToken);

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var threadId = context.Session?.StateBag.TryGetValue<string>(CosmosAgentSessionStore.AgUiThreadIdStateBagKey, out var id) ?? false
            ? id
            : "<unknown>";
        var stopwatch = Stopwatch.StartNew();
        var storedHistoryStopwatch = Stopwatch.StartNew();
        var storedIdentities = (await inner.GetMessagesAsync(context.Session, cancellationToken))
            .Select(GetStableIdentity)
            .Where(identity => identity is not null)
            .ToHashSet(StringComparer.Ordinal);
        storedHistoryStopwatch.Stop();

        var messagesToStore = (context.RequestMessages ?? [])
            .Concat(context.ResponseMessages ?? [])
            .Where(message =>
            {
                var identity = GetStableIdentity(message);
                return identity is null || storedIdentities.Add(identity);
            })
            .ToList();

        if (messagesToStore.Count == 0)
        {
            logger.LogInformation(
                "[Timing] History dedupe skipped write threadId={ThreadId} existingMessages={ExistingMessageCount} historyReadMs={HistoryReadMs} elapsedMs={ElapsedMs}",
                threadId,
                storedIdentities.Count,
                storedHistoryStopwatch.ElapsedMilliseconds,
                stopwatch.ElapsedMilliseconds);
            return;
        }

        var writeContext = new InvokedContext(
            context.Agent,
            context.Session,
            requestMessages: messagesToStore.Where(message => context.RequestMessages?.Contains(message) == true),
            responseMessages: messagesToStore.Where(message => context.ResponseMessages?.Contains(message) == true));
        var writeStopwatch = Stopwatch.StartNew();

        await inner.InvokedAsync(writeContext, cancellationToken);

        logger.LogInformation(
            "[Timing] History persisted threadId={ThreadId} existingMessages={ExistingMessageCount} storedMessages={StoredMessageCount} historyReadMs={HistoryReadMs} historyWriteMs={HistoryWriteMs} elapsedMs={ElapsedMs}",
            threadId,
            storedIdentities.Count,
            messagesToStore.Count,
            storedHistoryStopwatch.ElapsedMilliseconds,
            writeStopwatch.ElapsedMilliseconds,
            stopwatch.ElapsedMilliseconds);
    }

    private static string? GetStableIdentity(ChatMessage message)
    {
        var functionCallIds = message.Contents
            .OfType<FunctionCallContent>()
            .Select(content => content.CallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (functionCallIds.Length > 0)
        {
            return $"function-call:{message.Role}:{string.Join("|", functionCallIds)}";
        }

        var functionResultIds = message.Contents
            .OfType<FunctionResultContent>()
            .Select(content => content.CallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        return functionResultIds.Length > 0
            ? $"function-result:{message.Role}:{string.Join("|", functionResultIds)}"
            : !string.IsNullOrWhiteSpace(message.MessageId)
                ? $"message:{message.Role}:{message.MessageId}"
                : null;
    }
}
