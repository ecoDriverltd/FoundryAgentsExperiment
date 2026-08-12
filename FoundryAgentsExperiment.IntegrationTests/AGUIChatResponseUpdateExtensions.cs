using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.IntegrationTests;

internal static class AGUIChatResponseUpdateExtensions
{
    internal static ChatResponseUpdate AsChatResponseUpdateWithConversationId(this AgentResponseUpdate update)
    {
        var chatUpdate = update.AsChatResponseUpdate();

        if (chatUpdate.ConversationId is null &&
            chatUpdate.RawRepresentation is RunStartedEvent { ThreadId: { Length: > 0 } threadId })
        {
            chatUpdate.ConversationId = threadId;
        }

        return chatUpdate;
    }
}
