using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FoundryAgentsExperiment.Agent.AgentServices;

/// <summary>
/// One document per (conversation ID, user ID), storing the serialized <see cref="AgentSession"/>
/// and the metadata needed to list, recall, and resume that conversation.
/// </summary>
public sealed record AgentSessionEntry(
    string Id,
    string UserId,
    string SerializedSession,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    DateTimeOffset? SoftDeletedAt = null);

/// <summary>
/// Maps the agent session documents stored in the "agent-sessions" Cosmos DB container.
/// </summary>
public sealed class AgentSessionDbContext(DbContextOptions<AgentSessionDbContext> options) : DbContext(options)
{
    public const string ConversationIdStateBagKey = "conversation-id";

    public DbSet<AgentSessionEntry> Sessions => Set<AgentSessionEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<AgentSessionEntry>();
        entity.ToContainer("agent-sessions");
        entity.HasKey(entry => entry.Id);
        entity.HasPartitionKey(entry => entry.UserId);
        entity.Property(entry => entry.Id).ToJsonProperty("id");
        entity.Property(entry => entry.UserId).ToJsonProperty("userId");

        // Keep durable session state for one year.
        entity.HasDefaultTimeToLive((int)TimeSpan.FromDays(365).TotalSeconds);
    }
}

/// <summary>
/// Responses-facing <see cref="AgentSessionStore"/>. It persists the complete serialized
/// <see cref="AgentSession"/> by Responses conversation ID and user ID, including the framework-managed
/// in-memory chat history used to resume model conversations.
/// Cosmos items are limited to 2 MB; compaction-state persistence has been validated, but durable
/// transcript retention remains bounded by this store's UTF-8 byte-limit enforcement.
/// </summary>
public sealed class CosmosAgentSessionStore(
    IServiceScopeFactory scopeFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<CosmosAgentSessionStore> logger,
    IOptions<SessionPersistenceOptions> options) : AgentSessionStore
{
    public const string ConversationIdStateBagKey = "conversation-id";

    private readonly SessionPersistenceOptions options = options.Value;

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();
        var threadId = GetThreadId(conversationId, userId);
        var requestId = httpContextAccessor.HttpContext?.TraceIdentifier ?? "<no-http-request>";

        logger.LogInformation(
            "[SessionStore] GetSessionAsync requestId={RequestId} threadId={ThreadId} userId={UserId}",
            requestId,
            threadId,
            userId);

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var existing = await context.Sessions.AsNoTracking().SingleOrDefaultAsync(
            entry => entry.Id == threadId && entry.UserId == userId && entry.SoftDeletedAt == null,
            cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "[SessionStore] Loading persisted session threadId={ThreadId} userId={UserId} title={Title} serializedLength={SerializedLength}",
                threadId,
                userId,
                existing.Title,
                existing.SerializedSession.Length);
            LogSerializedSession("loaded", threadId, existing.SerializedSession, Encoding.UTF8.GetByteCount(existing.SerializedSession));
            using var document = JsonDocument.Parse(existing.SerializedSession);
            var restoredSession = await agent.DeserializeSessionAsync(document.RootElement.Clone(), cancellationToken: cancellationToken);
            restoredSession.StateBag.SetValue(ConversationIdStateBagKey, threadId);
            LogInMemoryChatHistory("restored", threadId, restoredSession);
            return restoredSession;
        }

        var session = await agent.CreateSessionAsync(cancellationToken);
        session.StateBag.SetValue(ConversationIdStateBagKey, threadId);
        logger.LogInformation(
            "[SessionStore] Created new session threadId={ThreadId} userId={UserId} conversationId={ConversationId}",
            threadId,
            userId,
            conversationId);
        LogInMemoryChatHistory("created", threadId, session);
        return session;
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var userId = httpContextAccessor.GetAgentUserId();
        var threadId = GetThreadId(conversationId, userId);
        var requestId = httpContextAccessor.HttpContext?.TraceIdentifier ?? "<no-http-request>";

        logger.LogInformation(
            "[SessionStore] SaveSessionAsync requestId={RequestId} threadId={ThreadId} userId={UserId}",
            requestId,
            threadId,
            userId);
        var serializationStopwatch = Stopwatch.StartNew();
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        serializationStopwatch.Stop();
        var serializedText = serialized.GetRawText();
        var serializedBytes = Encoding.UTF8.GetByteCount(serializedText);
        EnsureSessionFitsCosmosItemLimit(threadId, userId, serializedBytes);
        LogInMemoryChatHistory("saving", threadId, session);
        LogSerializedSession("saving", threadId, serializedText, serializedBytes);

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var now = DateTimeOffset.UtcNow;
        var existing = await context.Sessions.SingleOrDefaultAsync(
            entry => entry.Id == threadId && entry.UserId == userId,
            cancellationToken);

        if (existing is null)
        {
            var title = GetConversationTitle(serializedText);
            logger.LogInformation(
                "[SessionStore] Inserting session threadId={ThreadId} userId={UserId} title={Title} serializedBytes={SerializedBytes}",
                threadId,
                userId,
                title,
                serializedBytes);
            context.Sessions.Add(new AgentSessionEntry(
                Id: threadId,
                UserId: userId,
                SerializedSession: serializedText,
                Title: title,
                CreatedAt: now,
                LastUpdatedAt: now));
        }
        else
        {
            if (existing.SoftDeletedAt is not null)
            {
                logger.LogWarning(
                    "[SessionStore] Ignoring save for soft-deleted session threadId={ThreadId} userId={UserId} softDeletedAt={SoftDeletedAt}",
                    threadId,
                    userId,
                    existing.SoftDeletedAt);
                return;
            }

            var title = GetConversationTitle(serializedText, existing.Title);
            logger.LogInformation(
                "[SessionStore] Updating session threadId={ThreadId} userId={UserId} title={Title} serializedBytes={SerializedBytes}",
                threadId,
                userId,
                title,
                serializedBytes);
            context.Entry(existing).Property(entry => entry.SerializedSession).CurrentValue = serializedText;
            context.Entry(existing).Property(entry => entry.Title).CurrentValue = title;
            context.Entry(existing).Property(entry => entry.LastUpdatedAt).CurrentValue = now;
        }

        var saveStopwatch = Stopwatch.StartNew();
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "[Timing] Session saved requestId={RequestId} threadId={ThreadId} userId={UserId} serializedBytes={SerializedBytes} serializationMs={SerializationMs} cosmosSaveMs={CosmosSaveMs} elapsedMs={ElapsedMs}",
            requestId,
            threadId,
            userId,
            serializedBytes,
            serializationStopwatch.ElapsedMilliseconds,
            saveStopwatch.ElapsedMilliseconds,
            stopwatch.ElapsedMilliseconds);
    }

    public async Task<IReadOnlyList<AgentSessionEntry>> ListConversationsAsync(
        CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        return await context.Sessions
            .AsNoTracking()
            .Where(entry => entry.UserId == userId && entry.SoftDeletedAt == null)
            .OrderByDescending(entry => entry.LastUpdatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentSessionEntry?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();
        var threadId = GetThreadId(conversationId, userId);
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        return await context.Sessions.AsNoTracking().SingleOrDefaultAsync(
            entry => entry.Id == threadId && entry.UserId == userId && entry.SoftDeletedAt == null,
            cancellationToken);
    }

    public async Task<bool> SoftDeleteConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var userId = httpContextAccessor.GetAgentUserId();
        var threadId = GetThreadId(conversationId, userId);
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentSessionDbContext>();
        var existing = await context.Sessions.SingleOrDefaultAsync(
            entry => entry.Id == threadId && entry.UserId == userId && entry.SoftDeletedAt == null,
            cancellationToken);

        if (existing is null)
            return false;

        context.Entry(existing).Property(entry => entry.SoftDeletedAt).CurrentValue = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("[SessionStore] Soft-deleted session threadId={ThreadId} userId={UserId}", threadId, userId);
        return true;
    }

    public async Task<IReadOnlyList<ChatMessage>?> GetConversationMessagesAsync(
        AIAgent agent,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetConversationAsync(conversationId, cancellationToken);
        if (entry is null)
        {
            logger.LogInformation("[SessionStore] Conversation lookup did not find owned session conversationId={ConversationId}", conversationId);
            return null;
        }

        logger.LogInformation("[SessionStore] Loading transcript through the configured history provider conversationId={ConversationId}", conversationId);
        using var document = JsonDocument.Parse(entry.SerializedSession);
        var session = await agent.DeserializeSessionAsync(document.RootElement.Clone(), cancellationToken: cancellationToken);
        var chatHistoryProvider = agent.GetService<ChatHistoryProvider>()
            ?? throw new InvalidOperationException("The agent does not expose a chat history provider.");
        var invokingContext = new ChatHistoryProvider.InvokingContext(agent, session, requestMessages: []);
        return [.. (await chatHistoryProvider.InvokingAsync(invokingContext, cancellationToken))];
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

    private static string GetConversationTitle(string serializedSession, string? existingTitle = null)
    {
        if (!string.IsNullOrWhiteSpace(existingTitle) && existingTitle != "New conversation")
        {
            return existingTitle;
        }

        using var document = JsonDocument.Parse(serializedSession);
        var firstUserText = TryGetFirstCompactionUserText(document.RootElement);
        return Truncate(firstUserText, 60) ?? existingTitle ?? "New conversation";
    }

    private static string? TryGetFirstCompactionUserText(JsonElement session)
    {
        if (!session.TryGetProperty("stateBag", out var stateBag) ||
            !stateBag.TryGetProperty("SummarizationCompactionStrategy", out var compactionState) ||
            !compactionState.TryGetProperty("messagegroups", out var messageGroups))
        {
            return null;
        }

        return messageGroups
            .EnumerateArray()
            .Where(group => group.TryGetProperty("kind", out var kind) && kind.GetString() == "User")
            .SelectMany(group => group.GetProperty("messages").EnumerateArray())
            .SelectMany(message => message.GetProperty("contents").EnumerateArray())
            .Select(content => content.TryGetProperty("text", out var text) ? text.GetString() : null)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
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

    private void LogInMemoryChatHistory(string operation, string threadId, AgentSession session)
    {
        if (!session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages))
        {
            logger.LogWarning("[SessionStore] {Operation} threadId={ThreadId}: no in-memory chat history state was found", operation, threadId);
            return;
        }

        logger.LogInformation(
            "[SessionStore] {Operation} threadId={ThreadId}: in-memory history count={MessageCount} messages={Messages}",
            operation,
            threadId,
            messages.Count,
            string.Join(" | ", messages.Select(DescribeMessage)));
    }

    private void EnsureSessionFitsCosmosItemLimit(string threadId, string userId, int serializedBytes)
    {
        if (serializedBytes > SessionPersistenceOptions.CosmosItemLimitBytes)
        {
            throw new InvalidOperationException(
                $"The serialized agent session for thread '{threadId}' is {serializedBytes:N0} bytes, exceeding the Cosmos DB item limit of {SessionPersistenceOptions.CosmosItemLimitBytes:N0} bytes.");
        }

        if (serializedBytes > this.options.MaximumSerializedSessionBytes)
        {
            throw new InvalidOperationException(
                $"The serialized agent session for thread '{threadId}' is {serializedBytes:N0} bytes, exceeding the configured persistence limit of {this.options.MaximumSerializedSessionBytes:N0} bytes. Compaction must reduce the durable session before it can be saved.");
        }

        if (serializedBytes >= this.options.WarningThresholdBytes)
        {
            logger.LogWarning(
                "[SessionStore] Serialized session is approaching the Cosmos DB item limit threadId={ThreadId} userId={UserId} serializedBytes={SerializedBytes} warningThresholdBytes={WarningThresholdBytes} maximumSerializedSessionBytes={MaximumSerializedSessionBytes}",
                threadId,
                userId,
                serializedBytes,
                this.options.WarningThresholdBytes,
                this.options.MaximumSerializedSessionBytes);
        }
    }

    private void LogSerializedSession(string operation, string threadId, string serializedSession, int serializedBytes)
    {
        using var document = JsonDocument.Parse(serializedSession);
        var stateBagKeys = document.RootElement.TryGetProperty("stateBag", out var stateBag) && stateBag.ValueKind == JsonValueKind.Object
            ? string.Join(", ", stateBag.EnumerateObject().Select(property => property.Name))
            : "<none>";
        logger.LogInformation(
            "[SessionStore] {Operation} threadId={ThreadId}: serialized session bytes={SerializedBytes} stateBagKeys={StateBagKeys}",
            operation,
            threadId,
            serializedBytes,
            stateBagKeys);
    }

    private static string DescribeMessage(ChatMessage message)
    {
        var text = Truncate(message.Text, 160) ?? "<no text>";
        var contentKinds = string.Join(",", message.Contents.Select(content => content.GetType().Name));
        return $"{message.Role}[{message.MessageId ?? "<no-id>"}] text={text} contents={contentKinds}";
    }
}

public static class CosmosAgentSessionStoreExtensions
{
    public static IServiceCollection AddCosmosAgentSessionStore(this IServiceCollection services, string agentName)
    {
        services.AddOptions<SessionPersistenceOptions>()
            .BindConfiguration(SessionPersistenceOptions.SectionName)
            .Validate(options => options.CompactionTriggerTokens > 0, "Compaction trigger must be positive.")
            .Validate(options => options.CompactionMinimumPreservedGroups > 0, "At least one recent message group must be preserved.")
            .Validate(options => options.WarningThresholdBytes > 0 && options.WarningThresholdBytes < options.MaximumSerializedSessionBytes, "Warning threshold must be positive and below the persistence limit.")
            .Validate(options => options.MaximumSerializedSessionBytes > 0 && options.MaximumSerializedSessionBytes <= SessionPersistenceOptions.CosmosItemLimitBytes, "Persistence limit must be positive and not exceed the Cosmos DB item limit.")
            .ValidateOnStart();
        services.AddKeyedSingleton<CosmosAgentSessionStore>(agentName);

        return services;
    }
}
