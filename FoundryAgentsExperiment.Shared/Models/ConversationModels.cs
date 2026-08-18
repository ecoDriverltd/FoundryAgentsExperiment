using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Shared.Models;

public sealed record ConversationSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    string? LastRunId);

public sealed record ConversationDetail(
    string ConversationId,
    string Title,
    string? LastRunId,
    IReadOnlyList<ChatMessage> Messages);

public sealed record UpdateConversationContinuationRequest(string RunId, string? InitialUserPrompt);
