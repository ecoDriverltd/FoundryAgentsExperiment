using System.Collections.Concurrent;

namespace FoundryAgentsExperiment.Agent.AgentServices;

public sealed class ChatMessagePersistenceTracker
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> persistedPrompts = new();

    public bool TryMarkPersisted(string? key)
    {
        if (key is null) return false;

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in persistedPrompts)
        {
            if (now - entry.Value > Retention)
            {
                persistedPrompts.TryRemove(entry);
            }
        }

        return persistedPrompts.TryAdd(key, now);
    }
}

