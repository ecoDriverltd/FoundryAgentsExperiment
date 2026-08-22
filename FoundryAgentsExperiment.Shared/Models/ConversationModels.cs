using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Shared.Models;

public sealed record ConversationSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt);

public sealed record ConversationDetail(
    string ConversationId,
    string Title,
    IReadOnlyList<ChatMessage> Messages);
