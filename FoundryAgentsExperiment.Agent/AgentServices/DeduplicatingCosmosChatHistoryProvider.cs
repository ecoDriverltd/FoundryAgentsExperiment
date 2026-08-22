using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Agent.AgentServices;

public sealed class DeduplicatingCosmosChatHistoryProvider(CosmosChatHistoryProvider inner) : ChatHistoryProvider
{
    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default) =>
        inner.InvokingAsync(context, cancellationToken);

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var storedIdentities = (await inner.GetMessagesAsync(context.Session, cancellationToken))
            .Select(GetStableIdentity)
            .Where(identity => identity is not null)
            .ToHashSet(StringComparer.Ordinal);
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
            return;
        }

        var writeContext = new InvokedContext(
            context.Agent,
            context.Session,
            requestMessages: messagesToStore.Where(message => context.RequestMessages?.Contains(message) == true),
            responseMessages: messagesToStore.Where(message => context.ResponseMessages?.Contains(message) == true));
        await inner.InvokedAsync(writeContext, cancellationToken);
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
