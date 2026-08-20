using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Agents.AI;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace FoundryAgentsExperiment.Agent.AgentServices;

/// <summary>
/// Persists the model-replayable AG-UI transcript in Cosmos DB.
/// </summary>
/// <remarks>
/// Each record uses a deterministic ID and Cosmos create-only semantics. A <see cref="HttpStatusCode.Conflict"/>
/// therefore represents a retried AG-UI continuation that was already persisted, rather than an error or a duplicate.
/// This makes deduplication durable across process restarts and scaled-out agent instances.
/// </remarks>
public sealed class CosmosAgUiChatHistoryProvider : ChatHistoryProvider
{
    public const string DatabaseId = "agent-history";
    public const string ContainerId = "agent-transcript";
    private const string TenantId = "dev";
    private const int MessageTtlSeconds = 365 * 24 * 60 * 60;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly Container container;
    private readonly IHttpContextAccessor httpContextAccessor;

    public CosmosAgUiChatHistoryProvider(CosmosClient cosmosClient, IHttpContextAccessor httpContextAccessor)
        : base(
            provideOutputMessageFilter: FilterReplayableMessages,
            storeInputRequestMessageFilter: FilterRequestMessages,
            storeInputResponseMessageFilter: FilterReplayableMessages)
    {
        this.container = cosmosClient.GetContainer(DatabaseId, ContainerId);
        this.httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = GetScope(context.Session);
            var query = new QueryDefinition(
                    "SELECT VALUE c.message FROM c WHERE c.recordType = 'message' AND c.tenantId = @tenantId AND c.userId = @userId AND c.conversationId = @conversationId ORDER BY c.sequence")
                .WithParameter("@tenantId", scope.TenantId)
                .WithParameter("@userId", scope.UserId)
                .WithParameter("@conversationId", scope.ConversationId);

            var messages = new List<ChatMessage>();
            using var iterator = container.GetItemQueryIterator<string>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = scope.PartitionKey });

            while (iterator.HasMoreResults)
            {
                foreach (var serializedMessage in await iterator.ReadNextAsync(cancellationToken))
                {
                    var message = JsonSerializer.Deserialize<ChatMessage>(serializedMessage, JsonOptions);
                    if (message is not null)
                    {
                        messages.Add(message);
                    }
                }
            }

            return messages;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to load the AG-UI transcript.");
            throw;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scope = GetScope(context.Session);
            var messages = context.RequestMessages
                .Concat(context.ResponseMessages ?? [])
                .Select((message, index) => new PersistedMessage(message, index))
                .ToList();
            var turnAnchor = GetTurnAnchor(context.RequestMessages);

            foreach (var persistedMessage in messages)
            {
                var message = persistedMessage.Message;
                if (!IsReplayableTranscriptMessage(message))
                {
                    continue;
                }

                var key = GetIdempotencyKey(message, turnAnchor, persistedMessage.Index);
                var sequence = await AllocateSequenceAsync(scope, cancellationToken);
                var record = new TranscriptRecord(
                    Id: key,
                    TenantId: scope.TenantId,
                    UserId: scope.UserId,
                    ConversationId: scope.ConversationId,
                    Sequence: sequence,
                    Message: JsonSerializer.Serialize(message, JsonOptions),
                    Ttl: MessageTtlSeconds);

                try
                {
                    await container.CreateItemAsync(record, scope.PartitionKey, cancellationToken: cancellationToken);
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
                {
                    Logger.LogDebug(
                        "Skipped duplicate AG-UI transcript record {TranscriptRecordId} for conversation {ConversationId}.",
                        key,
                        scope.ConversationId);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to store the AG-UI transcript.");
            throw;
        }
    }

    private async Task<long> AllocateSequenceAsync(ConversationScope scope, CancellationToken cancellationToken)
    {
        const string counterId = "_transcript-sequence";

        try
        {
            // Increment first so ordinary writes avoid exception-driven control flow. A transactional batch cannot
            // use this patch result as the sequence property of a following create without server-side code.
            var response = await container.PatchItemAsync<TranscriptSequenceRecord>(
                counterId,
                scope.PartitionKey,
                [PatchOperation.Increment("/nextSequence", 1)],
                cancellationToken: cancellationToken);

            return response.Resource.NextSequence;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                await container.CreateItemAsync(
                    new TranscriptSequenceRecord(counterId, scope.TenantId, scope.UserId, scope.ConversationId, 1, MessageTtlSeconds),
                    scope.PartitionKey,
                    cancellationToken: cancellationToken);

                return 1;
            }
            catch (CosmosException createException) when (createException.StatusCode == HttpStatusCode.Conflict)
            {
                // Another request initialized the counter after this request observed its absence.
                var response = await container.PatchItemAsync<TranscriptSequenceRecord>(
                    counterId,
                    scope.PartitionKey,
                    [PatchOperation.Increment("/nextSequence", 1)],
                    cancellationToken: cancellationToken);

                return response.Resource.NextSequence;
            }
        }
    }

    private ConversationScope GetScope(AgentSession? session)
    {
        var conversationId = session?.StateBag.TryGetValue<string>(CosmosAgentSessionStore.ConversationIdStateBagKey, out var threadId) == true
            ? threadId
            : null;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new InvalidOperationException("The AG-UI conversation ID must be available in the agent session before chat history can be stored.");
        }

        var userId = httpContextAccessor.GetAgentUserId();
        return new ConversationScope(TenantId, userId, conversationId);
    }

    private ILogger<CosmosAgUiChatHistoryProvider> Logger =>
        httpContextAccessor.HttpContext?.RequestServices.GetService<ILogger<CosmosAgUiChatHistoryProvider>>()
        ?? NullLogger<CosmosAgUiChatHistoryProvider>.Instance;

    private static List<ChatMessage> FilterRequestMessages(IEnumerable<ChatMessage> messages)
    {
        var identifiedUserMessage = messages.LastOrDefault(message =>
            message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.MessageId));

        List<ChatMessage> messagesToPersist = [];
        if (identifiedUserMessage is not null)
        {
            messagesToPersist.Add(identifiedUserMessage);
        }

        // Browser-owned tools return through a later AG-UI continuation. Store their result as a Tool message
        // even if the transport representation supplied a different role.
        messagesToPersist.AddRange(messages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Where(result => !string.IsNullOrWhiteSpace(result.CallId))
            .Select(result => new ChatMessage(ChatRole.Tool, [result])));

        return messagesToPersist;
    }

    private static List<ChatMessage> FilterReplayableMessages(IEnumerable<ChatMessage> messages) =>
        [.. messages.Where(IsReplayableTranscriptMessage)];

    private static bool IsReplayableTranscriptMessage(ChatMessage message) =>
        !string.IsNullOrWhiteSpace(message.Text) ||
        message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent);

    private static string GetIdempotencyKey(ChatMessage message, string? turnAnchor, int index)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return message.Role == ChatRole.User
                ? $"user:{message.MessageId}"
                : $"message:{message.MessageId}";
        }

        var functionResultCallIds = message.Contents
            .OfType<FunctionResultContent>()
            .Select(result => result.CallId)
            .Where(callId => !string.IsNullOrWhiteSpace(callId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (functionResultCallIds.Length > 0)
        {
            return $"tool-result:{string.Join(':', functionResultCallIds)}";
        }

        var functionCallIds = message.Contents
            .OfType<FunctionCallContent>()
            .Select(call => call.CallId)
            .Where(callId => !string.IsNullOrWhiteSpace(callId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (functionCallIds.Length > 0)
        {
            return $"tool-call:{string.Join(':', functionCallIds)}";
        }

        if (!string.IsNullOrWhiteSpace(turnAnchor))
        {
            return $"turn:{turnAnchor}:message:{index:D4}";
        }

        var payload = JsonSerializer.Serialize(message, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return $"message:{message.Role.Value}:{index:D4}:{hash}";
    }

    private static string? GetTurnAnchor(IEnumerable<ChatMessage> requestMessages) =>
        requestMessages.LastOrDefault(message =>
            message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.MessageId))?.MessageId
        ?? requestMessages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .LastOrDefault(result => !string.IsNullOrWhiteSpace(result.CallId))?.CallId;

    private sealed record ConversationScope(string TenantId, string UserId, string ConversationId)
    {
        public PartitionKey PartitionKey => new PartitionKeyBuilder()
            .Add(TenantId)
            .Add(UserId)
            .Add(ConversationId)
            .Build();
    }

    private sealed record PersistedMessage(ChatMessage Message, int Index);

    private sealed record TranscriptRecord(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("tenantId")] string TenantId,
        [property: JsonProperty("userId")] string UserId,
        [property: JsonProperty("conversationId")] string ConversationId,
        [property: JsonProperty("sequence")] long Sequence,
        [property: JsonProperty("message")] string Message,
        [property: JsonProperty("ttl")] int Ttl)
    {
        [JsonProperty("recordType")]
        public string RecordType => "message";
    }

    private sealed record TranscriptSequenceRecord(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("tenantId")] string TenantId,
        [property: JsonProperty("userId")] string UserId,
        [property: JsonProperty("conversationId")] string ConversationId,
        [property: JsonProperty("nextSequence")] long NextSequence,
        [property: JsonProperty("ttl")] int Ttl)
    {
        [JsonProperty("recordType")]
        public string RecordType => "sequence";
    }
}
