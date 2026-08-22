using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using System.Collections.Concurrent;
using System.Text.Json;

namespace FoundryAgentsExperiment.SampleParityAgent;

public sealed class InspectableInMemoryAgentSessionStore : AgentSessionStore
{
    private readonly ConcurrentDictionary<string, JsonElement> sessions = new(StringComparer.Ordinal);

    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent,
        string sessionStoreId,
        CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent.Id, sessionStoreId);
        return sessions.TryGetValue(key, out var serialized)
            ? await agent.DeserializeSessionAsync(serialized, cancellationToken: cancellationToken)
            : await agent.CreateSessionAsync(cancellationToken);
    }

    public override async ValueTask SaveSessionAsync(
        AIAgent agent,
        string sessionStoreId,
        AgentSession session,
        CancellationToken cancellationToken = default)
    {
        sessions[GetKey(agent.Id, sessionStoreId)] = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
    }

    public override ValueTask DeleteSessionAsync(
        AIAgent agent,
        string sessionStoreId,
        CancellationToken cancellationToken = default)
    {
        sessions.TryRemove(GetKey(agent.Id, sessionStoreId), out _);
        return ValueTask.CompletedTask;
    }

    public bool TryGetSerializedSession(string agentId, string sessionStoreId, out JsonElement serializedSession) =>
        sessions.TryGetValue(GetKey(agentId, sessionStoreId), out serializedSession);

    public bool TryGetSerializedSessionByThreadId(string agentId, string threadId, out JsonElement serializedSession)
    {
        var keySuffix = $"::{threadId}";
        foreach (var (key, value) in sessions)
        {
            if (key.EndsWith(keySuffix, StringComparison.Ordinal) || key.EndsWith(threadId, StringComparison.Ordinal))
            {
                serializedSession = value;
                return true;
            }
        }

        serializedSession = default;
        return false;
    }

    public string[] GetStoredSessionKeys() => sessions.Keys.ToArray();

    private static string GetKey(string agentId, string sessionStoreId) => $"{agentId}:{sessionStoreId}";
}
