using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Agent;

public static class CosmosChatHistoryProviderFactory
{
    public static CosmosChatHistoryProvider Create(
        CosmosClient cosmosClient,
        IHttpContextAccessor httpContextAccessor,
        ChatMessagePersistenceTracker messagePersistenceTracker) =>
        new(
            cosmosClient,
            databaseId: "agent-history",
            containerId: "chat-history",
            stateInitializer: session => new CosmosChatHistoryProvider.State(
                conversationId: session?.StateBag.TryGetValue<string>(CosmosAgentSessionStore.ConversationIdStateBagKey, out var threadId) == true
                    ? threadId ?? Guid.NewGuid().ToString("n")
                    : Guid.NewGuid().ToString("n"),
                tenantId: "dev",
                userId: httpContextAccessor.GetAgentUserId()),
            provideOutputMessageFilter: FilterOutputHistoryMessages,
            storeInputRequestMessageFilter: messages => FilterRequestHistoryMessages(messages, messagePersistenceTracker),
            storeInputResponseMessageFilter: FilterResponseHistoryMessages)
        {
            MessageTtlSeconds = (int)TimeSpan.FromDays(365).TotalSeconds,
        };

    private static IEnumerable<ChatMessage> FilterOutputHistoryMessages(IEnumerable<ChatMessage> messages) =>
        messages.Where(IsReplayableTranscriptMessage).ToList();

    private static IEnumerable<ChatMessage> FilterRequestHistoryMessages(
        IEnumerable<ChatMessage> messages,
        ChatMessagePersistenceTracker messagePersistenceTracker)
    {
        var identifiedUserMessage = messages.LastOrDefault(message =>
            message.Role == ChatRole.User &&
            !string.IsNullOrWhiteSpace(message.MessageId));

        List<ChatMessage> messagesToPersist = [];
        if (identifiedUserMessage is not null && messagePersistenceTracker.TryMarkPersisted($"user:{identifiedUserMessage.MessageId}"))
        {
            messagesToPersist.Add(identifiedUserMessage);
        }

        foreach (var toolResult in messages
                     .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                     .Where(result => !string.IsNullOrWhiteSpace(result.CallId)))
        {
            if (messagePersistenceTracker.TryMarkPersisted($"tool-result:{toolResult.CallId}"))
            {
                messagesToPersist.Add(new ChatMessage(ChatRole.Tool, [toolResult]));
            }
        }

        return messagesToPersist;
    }

    private static IEnumerable<ChatMessage> FilterResponseHistoryMessages(IEnumerable<ChatMessage> messages) =>
        messages.Where(IsReplayableTranscriptMessage).ToList();

    private static bool IsReplayableTranscriptMessage(ChatMessage message) =>
        !string.IsNullOrWhiteSpace(message.Text) ||
        message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent);
}
