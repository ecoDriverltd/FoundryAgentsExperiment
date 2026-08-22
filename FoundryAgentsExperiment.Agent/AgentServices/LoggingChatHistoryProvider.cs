using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Agent.AgentServices;

public sealed class LoggingChatHistoryProvider(
    ChatHistoryProvider inner,
    ILogger<LoggingChatHistoryProvider> logger) : ChatHistoryProvider
{
    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var messages = await inner.InvokingAsync(context, cancellationToken);
        logger.LogInformation(
            "[History] Loaded {MessageCount} messages: {Messages}",
            messages.Count(),
            DescribeMessages(messages));
        return messages;
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "[History] Storing request messages={RequestMessages}; response messages={ResponseMessages}",
            DescribeMessages(context.RequestMessages),
            DescribeMessages(context.ResponseMessages ?? []));
        await inner.InvokedAsync(context, cancellationToken);
    }

    private static string DescribeMessages(IEnumerable<ChatMessage> messages) =>
        string.Join(" | ", messages.Select(message =>
            $"{message.Role}:id={message.MessageId ?? "<none>"}:text={message.Text ?? "<none>"}:contents={string.Join(",", message.Contents.Select(content => content.GetType().Name))}"));
}
