using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Shared.Models;

public sealed class AGUIContinuationState
{
    public string? ThreadId { get; private set; }

    public string? PreviousRunId { get; private set; }

    public void Restore(string threadId, string? previousRunId)
    {
        ThreadId = threadId;
        PreviousRunId = previousRunId;
    }

    public void Reset()
    {
        ThreadId = null;
        PreviousRunId = null;
    }

    // Conversation history belongs to the hosted Cosmos session, not the client. The client creates
    // a fresh AgentSession for each user-initiated turn because it accumulates streamed messages.
    // ThreadId and ParentRunId let that fresh session continue the same hosted AG-UI conversation.
    public AgentRunOptions? CreateRunOptions() =>
        ThreadId is null || PreviousRunId is null
            ? null
            : new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    RawRepresentationFactory = _ => new RunAgentInput
                    {
                        ThreadId = ThreadId,
                        ParentRunId = PreviousRunId,
                    },
                },
            };

    public void Observe(AgentResponseUpdate update)
    {
        // RUN_STARTED identifies this request. Its run ID is sent as ParentRunId when the user sends
        // their next message; the client does not retain messages from earlier user turns.
        if (update.AsChatResponseUpdate().RawRepresentation is RunStartedEvent runStarted)
        {
            ThreadId = runStarted.ThreadId;
            PreviousRunId = runStarted.RunId;
        }
    }
}
