using System.Collections.Concurrent;

namespace FoundryAgentsExperiment.Agent.AgentServices;

public sealed class AgUiFailureDiagnostics
{
    private readonly ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> requests = new(StringComparer.Ordinal);

    public void Record(string userId, Exception exception) => failures[userId] = exception.ToString();

    public void RecordRequest(string userId, string request) => requests[userId] = request;

    public string? Get(string userId)
    {
        failures.TryGetValue(userId, out var failure);
        requests.TryGetValue(userId, out var request);
        return failure is null
            ? null
            : request is null
                ? failure
                : $"Last AG-UI request:{Environment.NewLine}{request}{Environment.NewLine}{Environment.NewLine}Failure:{Environment.NewLine}{failure}";
    }
}