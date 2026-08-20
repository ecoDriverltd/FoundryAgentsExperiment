using Microsoft.EntityFrameworkCore;

namespace FoundryAgentsExperiment.Agent.AgentServices;

/// <summary>
/// One lightweight document per conversation, stored in a separate container from the full chat
/// history so listing a user's conversations doesn't require scanning/deserializing message bodies.
/// </summary>
public sealed record ConversationIndexEntry(
    string Id,
    string UserId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    string? LastRunId = null);

/// <summary>
/// Maps the conversation index documents stored in the existing Cosmos DB container.
/// </summary>
public sealed class ConversationIndexDbContext(DbContextOptions<ConversationIndexDbContext> options) : DbContext(options)
{
    public DbSet<ConversationIndexEntry> Conversations => Set<ConversationIndexEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ConversationIndexEntry>();
        entity.ToContainer("conversation-index");
        entity.HasKey(entry => entry.Id);
        entity.HasPartitionKey(entry => entry.UserId);
        entity.Property(entry => entry.Id).ToJsonProperty("id");
        entity.Property(entry => entry.UserId).ToJsonProperty("userId");

        // Mirrors the retention window on CosmosChatHistoryProvider.MessageTtlSeconds (Agent.cs) -
        // a conversation's index entry should stick around at least as long as its messages do,
        // otherwise it could disappear from the /conversations list while its history is still
        // resumable. Only takes effect if the container's TTL is actually enabled/synced (see note
        // in Agent.cs); this Fluent API setting only applies when EF Core provisions the container
        // itself (e.g. via EnsureCreatedAsync) - for an already-existing container, update its
        // DefaultTimeToLive directly (Bicep/Portal/Data Explorer) to match.
        entity.HasDefaultTimeToLive((int)TimeSpan.FromDays(365).TotalSeconds);
    }
}

/// <summary>
/// Upserts/queries the conversation index container backing the <c>/conversations</c> list endpoint.
/// </summary>
public sealed class CosmosConversationIndexStore(IServiceScopeFactory scopeFactory)
{
    public async Task RecordConversationTurnAsync(
        string conversationId,
        string userId,
        string? firstUserMessageText,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConversationIndexDbContext>();
        var now = DateTimeOffset.UtcNow;
        var existing = await context.Conversations.SingleOrDefaultAsync(
            entry => entry.Id == conversationId && entry.UserId == userId,
            cancellationToken);

        var title = existing?.Title == "New conversation"
            ? Truncate(firstUserMessageText, 60) ?? existing.Title
            : existing?.Title
                ?? Truncate(firstUserMessageText, 60)
                ?? "New conversation";

        if (existing is null)
        {
            context.Conversations.Add(new ConversationIndexEntry(
                Id: conversationId,
                UserId: userId,
                Title: title,
                CreatedAt: now,
                LastUpdatedAt: now,
                LastRunId: null));
        }
        else
        {
            context.Entry(existing).Property(entry => entry.Title).CurrentValue = title;
            context.Entry(existing).Property(entry => entry.LastUpdatedAt).CurrentValue = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> UpdateLastRunIdAsync(
        string conversationId,
        string userId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConversationIndexDbContext>();
        var existing = await context.Conversations.SingleOrDefaultAsync(
            entry => entry.Id == conversationId && entry.UserId == userId,
            cancellationToken);

        if (existing is null)
        {
            return false;
        }

        context.Entry(existing).Property(entry => entry.LastRunId).CurrentValue = runId;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ConversationIndexEntry>> ListConversationsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConversationIndexDbContext>();

        return await context.Conversations
            .AsNoTracking()
            .Where(entry => entry.UserId == userId)
            .OrderByDescending(entry => entry.LastUpdatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Looks up a single conversation, scoped to the caller's userId. Used to verify ownership
    /// before returning a conversation's full message history (see /get-chat-conversation in Agent.cs).
    /// </summary>
    public async Task<ConversationIndexEntry?> GetConversationAsync(
        string conversationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConversationIndexDbContext>();

        return await context.Conversations
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Id == conversationId && entry.UserId == userId, cancellationToken);
    }

    private static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
    }
}
