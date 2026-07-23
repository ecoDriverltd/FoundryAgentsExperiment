// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleAgent;

/// <summary>
/// In-memory implementation of <see cref="Microsoft.Agents.AI.Hosting.AgentSessionStore"/> —
/// the contract used by <c>MapAGUI</c> to persist/restore AG-UI conversation sessions across turns.
/// </summary>
/// <remarks>
/// This is distinct from <see cref="Microsoft.Agents.AI.Foundry.Hosting.InMemoryAgentSessionStore"/>,
/// which implements a different, Foundry-specific <c>AgentSessionStore</c> contract used by
/// <c>MapFoundryResponses</c>/<c>MapOpenAIConversations</c>. The two are not interchangeable.
/// All stored sessions are lost on process restart — replace with a durable store for production.
/// </remarks>
public sealed class SimpleInMemoryAgentSessionStore(ILogger<SimpleInMemoryAgentSessionStore> logger) : AgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> sessions = new();

    /// <inheritdoc/>
    public override async ValueTask SaveSessionAsync(
        AIAgent agent,
        string conversationId,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        JsonElement serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        this.sessions[conversationId] = serialized;

        var payload = serialized.GetRawText();
        logger.LogInformation(
            "[SessionStore] SAVE conversationId={ConversationId} payloadLength={Length} payload={Payload}",
            conversationId,
            payload.Length,
            payload.Length > 2000 ? payload[..2000] + "...(truncated)" : payload);
    }

    /// <inheritdoc/>
    public override ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        bool found = this.sessions.TryGetValue(conversationId, out JsonElement serialized);
        logger.LogInformation(
            "[SessionStore] GET conversationId={ConversationId} found={Found} knownKeys=[{Keys}]",
            conversationId,
            found,
            string.Join(", ", this.sessions.Keys));

        return found
            ? agent.DeserializeSessionAsync(serialized, cancellationToken: cancellationToken)
            : agent.CreateSessionAsync(cancellationToken: cancellationToken);
    }
}
