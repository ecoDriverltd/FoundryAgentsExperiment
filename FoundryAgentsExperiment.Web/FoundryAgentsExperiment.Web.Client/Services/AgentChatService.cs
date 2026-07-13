using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Web.Client.Services;

/// <summary>
/// Encapsulates the AG-UI turn pattern validated by the integration tests:
/// each turn sends only [system + user] to the agent; Foundry manages the
/// conversation history server-side, keyed by the server-assigned thread ID.
/// </summary>
public class AgentChatService(IChatClient chatClient)
{
    /// <summary>
    /// Streams the agent's response for a single turn.
    /// </summary>
    /// <param name="threadId">
    /// The server-assigned Foundry thread ID from the previous turn, or <see langword="null"/>
    /// on the first turn of a new conversation. The server will assign one and return it
    /// via <paramref name="onThreadIdAssigned"/>.
    /// </param>
    /// <param name="systemPrompt">Fresh context injected at the start of every turn.</param>
    /// <param name="userText">The user's message for this turn.</param>
    /// <param name="onThreadIdAssigned">
    /// Invoked the first time the server returns a thread ID in the response stream.
    /// Use this to capture and persist the real Foundry thread ID.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<string> StreamAsync(
        string? threadId,
        string systemPrompt,
        string userText,
        Func<string, Task>? onThreadIdAssigned = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var options = new ChatOptions { ConversationId = threadId };

        var messages = new[]
        {
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userText)
        };

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, ct))
        {
            // Capture the server-assigned thread ID the first time it appears
            if (onThreadIdAssigned is not null && !string.IsNullOrEmpty(update.ConversationId))
            {
                await onThreadIdAssigned(update.ConversationId);
                onThreadIdAssigned = null; // fire once only
            }

            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }
}
