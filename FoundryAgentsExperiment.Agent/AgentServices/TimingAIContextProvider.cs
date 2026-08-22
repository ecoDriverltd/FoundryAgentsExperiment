using Microsoft.Agents.AI;
using System.Diagnostics;

namespace FoundryAgentsExperiment.Agent.AgentServices;

public sealed class TimingAIContextProvider(
    string name,
    AIContextProvider inner,
    ILogger<TimingAIContextProvider> logger) : AIContextProvider
{
    public override IReadOnlyList<string> StateKeys => inner.StateKeys;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var aiContext = await inner.InvokingAsync(context, cancellationToken);
        logger.LogInformation(
            "[Timing] Context provider loaded name={Name} elapsedMs={ElapsedMs}",
            name,
            stopwatch.ElapsedMilliseconds);
        return aiContext;
    }

    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await inner.InvokedAsync(context, cancellationToken);
        logger.LogInformation(
            "[Timing] Context provider stored name={Name} elapsedMs={ElapsedMs}",
            name,
            stopwatch.ElapsedMilliseconds);
    }
}
