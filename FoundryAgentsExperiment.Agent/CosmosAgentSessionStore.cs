using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace FoundryAgentsExperiment.Agent;

/// <summary>
/// One document per (conversationId, userId), storing the small serialized <see cref="AgentSession"/>
/// (ConversationId + StateBag provider bookkeeping - e.g. CompactionProvider's running summary state).
/// Never contains chat messages: those live exclusively in CosmosChatHistoryProvider's "chat-history"
/// container, addressed by the conversationId/tenantId/userId carried in this session's StateBag.
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

        // Mirrors the retention window on CosmosChatHistoryProvider.MessageTtlSeconds (Agent.cs) and
        // ConversationIndexDbContext - see the equivalent note there for why TTL sync with an
        // already-existing container needs to be done via Bicep/Portal/Data Explorer instead.
        entity.HasDefaultTimeToLive((int)TimeSpan.FromDays(365).TotalSeconds);
    }
}

/// <summary>
/// AG-UI-facing <see cref="AgentSessionStore"/> for Mode 2 (client-managed chat history via
/// <c>CosmosChatHistoryProvider</c>). Persists ONLY the small serialized <see cref="AgentSession"/>
/// (ConversationId + StateBag provider bookkeeping) in the "agent-sessions" container - the transcript's
/// durable home remains the ChatHistoryProvider, so this store deliberately never stores messages,
/// avoiding duplication of the "chat-history" container's content.
///
/// NOTE: pre-tagging the session via <c>ChatClientAgent.CreateSessionAsync(conversationId, ct)</c> was
/// tried and rejected - it causes ChatClientAgent to feed that conversationId into the underlying
/// Responses API's <c>previous_response_id</c> parameter, which broke turn 2 with an HTTP 400
/// (invalid_request_error: string_above_max_length) since our threadId format exceeds the 64-char
/// limit expected there. Instead, the threadId is stashed on the session's
/// <see cref="AgentSession.StateBag"/> (per-session storage, not ambient/thread-flow state) under
/// <see cref="ConversationIdStateBagKey"/>, which the ChatHistoryProvider's stateInitializer in
/// Agent.cs reads back out via the same key.
///
/// SaveSessionAsync is the once-per-turn hook (guaranteed by MapAGUIServer to fire exactly once, after
/// streaming completes) for both persisting the serialized session and updating the lightweight
/// conversation index used by the /conversations list.
/// </summary>
public sealed class CosmosAgentSessionStore(
    IServiceScopeFactory scopeFactory,
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
        var userId = httpContextAccessor.GetAgentUserId();

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var existing = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(
            entry => entry.Id == conversationId && entry.UserId == userId,
            cancellationToken);

        if (existing is not null)
        {
            using var document = JsonDocument.Parse(existing.SerializedSession);
            return await agent.DeserializeSessionAsync(document.RootElement.Clone(), cancellationToken: cancellationToken);
        }

        var session = await agent.CreateSessionAsync(cancellationToken);
        session.StateBag.SetValue(ConversationIdStateBagKey, conversationId);
        return session;
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();

        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        var serializedText = serialized.GetRawText();

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var now = DateTimeOffset.UtcNow;
        var existing = await context.Sessions.SingleOrDefaultAsync(
            entry => entry.Id == conversationId && entry.UserId == userId,
            cancellationToken);

        if (existing is null)
        {
            context.Sessions.Add(new AgentSessionEntry(
                Id: conversationId,
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

        await conversationIndexStore.RecordConversationTurnAsync(conversationId, userId, firstUserMessageText, cancellationToken);
    }

    public override async ValueTask DeleteSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var existing = await context.Sessions.SingleOrDefaultAsync(
            entry => entry.Id == conversationId && entry.UserId == userId,
            cancellationToken);

        if (existing is not null)
        {
            context.Sessions.Remove(existing);
            await context.SaveChangesAsync(cancellationToken);
        }
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
