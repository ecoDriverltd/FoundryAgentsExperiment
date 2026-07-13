using Microsoft.Agents.AI.Foundry.Hosting;

/// <summary>
/// Registration helpers for developer-only local hosted-agent contributor utilities.
/// </summary>
public static class HostedContributorSetupExtensions
{
    /// <summary>
    /// Registers services that let a hosted Foundry agent run outside the Foundry platform during local debugging.
    /// </summary>
    public static IServiceCollection AddDevTemporaryLocalContributorSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

#pragma warning disable MAAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        services.AddSingleton<HostedSessionIsolationKeyProvider, DevTemporaryLocalUserIdProvider>();
#pragma warning restore MAAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        return services;
    }
}
