using Microsoft.Agents.AI.Hosting;

namespace FoundryAgentsExperiment.Agent.AgentExtensions;

/// <summary>
/// Supplies the per-caller isolation key that <c>MapAGUIServer</c> uses to automatically wrap the
/// registered <see cref="Microsoft.Agents.AI.Hosting.AgentSessionStore"/> in an
/// <c>IsolationKeyScopedAgentSessionStore</c>, so conversations from one caller can never be read or
/// overwritten by another. Reads the same <c>x-agent-user-id</c> header convention used elsewhere in
/// this app (see <see cref="AgentUserId"/>).
/// </summary>
public sealed class AgentUserIdSessionIsolationKeyProvider(IHttpContextAccessor httpContextAccessor)
    : SessionIsolationKeyProvider
{
    public override ValueTask<string?> GetSessionIsolationKeyAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(httpContextAccessor.GetAgentUserId());
}
