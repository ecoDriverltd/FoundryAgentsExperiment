namespace FoundryAgentsExperiment.Web.Client.Models;

public record ConversationSummary
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The server-assigned Foundry thread ID returned in the first turn's response stream.
    /// Passed as ChatOptions.ConversationId on every subsequent turn so Foundry can
    /// replay its server-side history.
    /// </summary>
    public string? ThreadId { get; set; }
}
