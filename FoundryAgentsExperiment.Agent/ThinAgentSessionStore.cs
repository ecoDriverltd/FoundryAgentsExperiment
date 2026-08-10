using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Agent;

/// <summary>
/// AG-UI-facing <see cref="AgentSessionStore"/> for Mode 2 (client-managed chat history via
/// <c>CosmosChatHistoryProvider</c>). The transcript's durable home is the ChatHistoryProvider, so this
/// store no longer needs to persist or look up anything - it just needs to carry the AG-UI wire
/// <c>threadId</c> through to the ChatHistoryProvider's stateInitializer.
///
/// NOTE: pre-tagging the session via <c>ChatClientAgent.CreateSessionAsync(conversationId, ct)</c> was
/// tried and rejected - it causes ChatClientAgent to feed that conversationId into the underlying
/// Responses API's <c>previous_response_id</c> parameter, which broke turn 2 with an HTTP 400
/// (invalid_request_error: string_above_max_length) since our threadId format exceeds the 64-char
/// limit expected there. Instead, the threadId is stashed on the newly-created session's
/// <see cref="AgentSession.StateBag"/> (per-session storage, not ambient/thread-flow state) under
/// <see cref="ConversationIdStateBagKey"/>, which the ChatHistoryProvider's stateInitializer in
/// Agent.cs reads back out via the same key. The session itself is created via the plain
/// parameterless CreateSessionAsync().
///
/// SaveSessionAsync is repurposed as the once-per-turn hook (guaranteed by MapAGUIServer to fire exactly
/// once, after streaming completes) for updating the lightweight conversation index used by the
/// /conversations list.
/// </summary>
public sealed class ThinAgentSessionStore(
    CosmosConversationIndexStore conversationIndexStore,
    IHttpContextAccessor httpContextAccessor) : AgentSessionStore
{
    /// <summary>
    /// StateBag key under which the AG-UI threadId is stashed on each newly-created session. Read by
    /// the ChatHistoryProvider's stateInitializer (see Agent.cs) since ChatClientAgentSession.ConversationId
    /// can't be relied on for this without breaking the underlying Responses API call (see class doc).
    /// </summary>
    public const string ConversationIdStateBagKey = "ag-ui-thread-id";

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var session = await agent.CreateSessionAsync(cancellationToken);
        session.StateBag.SetValue(ConversationIdStateBagKey, conversationId);
        return session;
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();

        string? firstUserMessageText = null;
        if (session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages))
        {
            firstUserMessageText = messages
                .FirstOrDefault(message => message.Role == ChatRole.User)
                ?.Text;
        }

        await conversationIndexStore.RecordConversationTurnAsync(conversationId, userId, firstUserMessageText, cancellationToken);
    }

    public override ValueTask DeleteSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public static class ThinAgentSessionStoreExtensions
{
    public static IServiceCollection AddThinAgentSessionStore(this IServiceCollection services, string agentName)
    {
        services.AddKeyedSingleton<AgentSessionStore, ThinAgentSessionStore>(agentName);
        services.AddSingleton<AgentSessionStore, ThinAgentSessionStore>();
        return services;
    }
}
