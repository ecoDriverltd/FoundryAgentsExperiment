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
        if (update.AsChatResponseUpdate().RawRepresentation is RunStartedEvent runStarted)
        {
            ThreadId = runStarted.ThreadId;
            PreviousRunId = runStarted.RunId;
        }
    }
}
