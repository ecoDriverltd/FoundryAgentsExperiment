using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Agent.AgentServices;

public sealed class IdempotentInMemoryChatHistoryProvider : ChatHistoryProvider
{
    private readonly InMemoryChatHistoryProvider inner = new();

    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default) =>
        inner.InvokingAsync(context, cancellationToken);

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var storedMessages = inner.GetMessages(context.Session);
        var storedIdentities = storedMessages
            .Select(GetStableIdentity)
            .Where(identity => identity is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var message in (context.RequestMessages ?? []).Concat(context.ResponseMessages ?? []))
        {
            var identity = GetStableIdentity(message);
            if (identity is null || storedIdentities.Add(identity))
            {
                storedMessages.Add(message);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string? GetStableIdentity(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return $"message:{message.Role}:{message.MessageId}";
        }

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
            : null;
    }
}
