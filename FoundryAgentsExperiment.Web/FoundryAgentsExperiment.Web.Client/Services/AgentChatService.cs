using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Web.Client.Services;

/// <summary>
/// Encapsulates the AG-UI turn pattern validated by the integration tests, using the AIAgent /
/// AgentSession abstraction (rather than a raw IChatClient) so client-side tool calls and
/// generative UI content can be layered on later. Each turn sends only [system + user] to the
/// agent; Foundry manages the conversation history server-side, keyed by the server-assigned
/// thread ID carried by the AgentSession.
/// </summary>
public class AgentChatService(ChatClientAgent agent)
{
    /// <summary>
    /// Creates a session for a new conversation, or one that resumes an existing Foundry thread.
    /// </summary>
    /// <param name="threadId">
    /// The server-assigned Foundry thread ID from a previous conversation, or <see langword="null"/>
    /// to start a brand-new conversation.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask<AgentSession> CreateSessionAsync(string? threadId, CancellationToken ct = default) =>
        threadId is { Length: > 0 }
            ? agent.CreateSessionAsync(threadId, ct)
            : agent.CreateSessionAsync(ct);

    /// <summary>
    /// Streams the agent's response for a single turn on an existing <see cref="AgentSession"/>.
    /// </summary>
    /// <param name="session">The session for this conversation, from <see cref="CreateSessionAsync"/>.</param>
    /// <param name="systemPrompt">Fresh context injected at the start of every turn.</param>
    /// <param name="userText">The user's message for this turn.</param>
    /// <param name="onThreadIdAssigned">
    /// Invoked the first time the server returns a thread ID in the response stream.
    /// Use this to capture and persist the real Foundry thread ID.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<string> StreamAsync(
        AgentSession session,
        string systemPrompt,
        string userText,
        Func<string, Task>? onThreadIdAssigned = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userText)
        ];

        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session, cancellationToken: ct))
        {
            ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

            // Capture the server-assigned thread ID the first time it appears
            if (onThreadIdAssigned is not null && !string.IsNullOrEmpty(chatUpdate.ConversationId))
            {
                await onThreadIdAssigned(chatUpdate.ConversationId);
                onThreadIdAssigned = null; // fire once only
            }

            foreach (AIContent content in update.Contents)
            {
                if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    yield return textContent.Text;
            }
        }
    }
}
