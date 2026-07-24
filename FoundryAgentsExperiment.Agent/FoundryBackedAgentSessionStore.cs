using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SimpleAgent;

/// <summary>
/// Adapts a Foundry-specific <see cref="Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore"/>
/// (e.g. <see cref="Microsoft.Agents.AI.Foundry.Hosting.FileSystemAgentSessionStore"/>) to the
/// <see cref="Microsoft.Agents.AI.Hosting.AgentSessionStore"/> contract that <c>MapAGUI</c> resolves,
/// giving AG-UI the same on-disk durability and per-user isolation that <c>AddFoundryResponses</c>
/// already provides by default for the Responses/OpenAI Conversations endpoints.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.Agents.AI.Foundry.Hosting.HostedSessionIsolationKeyProvider"/> can't be reused
/// here — its <c>GetKeysAsync</c> requires Responses-API-specific request types
/// (<c>ResponseContext</c>/<c>CreateResponse</c>) that don't exist in the AG-UI pipeline. Instead, the
/// caller's user id is read directly from the <c>x-agent-user-id</c> header, matching the convention
/// used elsewhere in this app (see <see cref="FoundrySettings"/>).
/// </remarks>
public sealed class FoundryBackedAgentSessionStore(
    Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore inner,
    IHttpContextAccessor httpContextAccessor)
    : Microsoft.Agents.AI.Hosting.AgentSessionStore
{
    private const string UserIdHeaderName = "x-agent-user-id";

    public override ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
        => inner.SaveSessionAsync(agent, conversationId, session, GetUserId(), cancellationToken);

    public override ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
        => inner.GetSessionAsync(agent, conversationId, GetUserId(), cancellationToken);

    public override async ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore only exposes Save/GetSessionAsync -
        // there is no delete/remove primitive, and FileSystemAgentSessionStore's session file path
        // is a private implementation detail we can't reach from here. Emulate a delete by replacing
        // the persisted state with a brand-new session: GetSessionAsync already treats a missing or
        // empty session file as "no session", so overwriting with a freshly created one is
        // functionally equivalent for every caller of the outer store.
        AgentSession freshSession = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        await inner.SaveSessionAsync(agent, sessionStoreId, freshSession, GetUserId(), cancellationToken).ConfigureAwait(false);
    }

    private string? GetUserId()
        => httpContextAccessor.HttpContext?.Request.Headers[UserIdHeaderName].ToString() is { Length: > 0 } userId
            ? userId
            : null;
}

public static class FoundryBackedAgentSessionStoreExtensions
{
    public static IServiceCollection AddFoundryBackedAgentSessionStore(this IServiceCollection services, string agentName)
    {
        services.AddHttpContextAccessor();

        FileSystemAgentSessionStore foundrySessionStore = FileSystemAgentSessionStore.CreateDefault();
        services.TryAddSingleton<Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore>(foundrySessionStore);
        services.TryAddKeyedSingleton<Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore>(agentName, foundrySessionStore);

        services.AddSingleton<Microsoft.Agents.AI.Hosting.AgentSessionStore>(sp =>
        {
            var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
            return new FoundryBackedAgentSessionStore(
                sp.GetRequiredService<Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore>(),
                httpContextAccessor);
        });

        services.AddKeyedSingleton<Microsoft.Agents.AI.Hosting.AgentSessionStore>(
            agentName,
            (sp, _) => new FoundryBackedAgentSessionStore(
                sp.GetRequiredKeyedService<Microsoft.Agents.AI.Foundry.Hosting.AgentSessionStore>(agentName),
                sp.GetRequiredService<IHttpContextAccessor>()));

        return services;
    }
}