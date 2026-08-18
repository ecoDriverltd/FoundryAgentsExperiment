using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace FoundryAgentsExperiment.Agent;

/// <summary>
/// One document per (conversation ID, user ID), storing the serialized <see cref="AgentSession"/>
/// and provider bookkeeping. Chat messages remain in CosmosChatHistoryProvider's separate
/// "chat-history" container.
/// </summary>
public sealed record AgentSessionEntry(
    string Id,
    string UserId,
    string SerializedSession,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt);

/// <summary>
/// Maps the agent session documents stored in the "agent-sessions" Cosmos DB container.
/// </summary>
public sealed class AgentSessionDbContext(DbContextOptions<AgentSessionDbContext> options) : DbContext(options)
{
    public DbSet<AgentSessionEntry> Sessions => Set<AgentSessionEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AgentSessionEntry>();
        entity.ToContainer("agent-sessions");
        entity.HasKey(entry => entry.Id);
        entity.HasPartitionKey(entry => entry.UserId);
        entity.Property(entry => entry.Id).ToJsonProperty("id");
        entity.Property(entry => entry.UserId).ToJsonProperty("userId");

        // Keep serialized state aligned with the conversation index and chat-history retention window.
        entity.HasDefaultTimeToLive((int)TimeSpan.FromDays(365).TotalSeconds);
    }
}

/// <summary>
/// AG-UI-facing <see cref="AgentSessionStore"/>. It persists the small serialized
/// <see cref="AgentSession"/> by AG-UI thread ID and user ID. Chat messages remain in the
/// server-managed CosmosChatHistoryProvider transcript.
/// </summary>
public sealed class CosmosAgentSessionStore(
    IServiceScopeFactory scopeFactory,
    CosmosConversationIndexStore conversationIndexStore,
    IHttpContextAccessor httpContextAccessor) : AgentSessionStore
{
    /// <summary>
    /// StateBag key through which the AG-UI thread ID identifies the server-managed transcript.
    /// This is deliberately separate from MEAI's ConversationId.
    /// </summary>
    public const string ConversationIdStateBagKey = "ag-ui-thread-id";

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();
        var threadId = GetThreadId(conversationId, userId);

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var existing = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(
            entry => entry.Id == threadId && entry.UserId == userId,
            cancellationToken);

        if (existing is not null)
        {
            using var document = JsonDocument.Parse(existing.SerializedSession);
            return await agent.DeserializeSessionAsync(document.RootElement.Clone(), cancellationToken: cancellationToken);
        }

        var session = await agent.CreateSessionAsync(cancellationToken);
        session.StateBag.SetValue(ConversationIdStateBagKey, threadId);
        return session;
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();
        var threadId = GetThreadId(conversationId, userId);

        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        var serializedText = serialized.GetRawText();

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var now = DateTimeOffset.UtcNow;
        var existing = await context.Sessions.SingleOrDefaultAsync(
            entry => entry.Id == threadId && entry.UserId == userId,
            cancellationToken);

        if (existing is null)
        {
            context.Sessions.Add(new AgentSessionEntry(
                Id: threadId,
                UserId: userId,
                SerializedSession: serializedText,
                CreatedAt: now,
                LastUpdatedAt: now));
        }
        else
        {
            context.Entry(existing).Property(entry => entry.SerializedSession).CurrentValue = serializedText;
            context.Entry(existing).Property(entry => entry.LastUpdatedAt).CurrentValue = now;
        }

        await context.SaveChangesAsync(cancellationToken);

        string? firstUserMessageText = null;
        if (session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages))
        {
            firstUserMessageText = messages
                .FirstOrDefault(message => message.Role == ChatRole.User)
                ?.Text;
        }

        await conversationIndexStore.RecordConversationTurnAsync(threadId, userId, firstUserMessageText, cancellationToken);
    }

    public override async ValueTask DeleteSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();
        var threadId = GetThreadId(conversationId, userId);

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var existing = await context.Sessions.SingleOrDefaultAsync(
            entry => entry.Id == threadId && entry.UserId == userId,
            cancellationToken);

        if (existing is not null)
        {
            context.Sessions.Remove(existing);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private static string GetThreadId(string conversationId, string userId)
    {
        var isolationPrefix = userId + "::";
        return conversationId.StartsWith(isolationPrefix, StringComparison.Ordinal)
            ? conversationId[isolationPrefix.Length..]
            : conversationId;
    }
}

public static class CosmosAgentSessionStoreExtensions
{
    public static IServiceCollection AddCosmosAgentSessionStore(this IServiceCollection services, string agentName)
    {
        services.AddKeyedSingleton<AgentSessionStore, CosmosAgentSessionStore>(agentName);
        services.AddSingleton<AgentSessionStore, CosmosAgentSessionStore>();
        return services;
    }
}
