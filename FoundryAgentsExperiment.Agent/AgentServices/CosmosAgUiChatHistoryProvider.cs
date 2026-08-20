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
/// AG-UI identifies protocol requests with thread and run identifiers, and may resend a continuation after a
/// transport retry or when a browser-owned tool completes. This provider maps the AG-UI thread stored in the
/// <see cref="AgentSession"/> to a durable Cosmos conversation partition, then stores only the messages needed
/// to reconstruct the model-visible transcript: conversational text, function calls, and function results.
///
/// Each transcript record has a deterministic ID and is created rather than upserted. A
/// <see cref="HttpStatusCode.Conflict"/> therefore means the same AG-UI continuation was already persisted,
/// not that a second copy should be written. This provides durable idempotency across retries, process restarts,
/// and scaled-out agent instances without an in-memory duplicate-tracking cache.
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

    /// <summary>
    /// Loads the persisted transcript in deterministic order before the Agent Framework invokes the model.
    /// </summary>
    /// <remarks>
    /// Deterministic order means the model receives prior user messages, assistant responses, function calls, and
    /// function results in the same chronological sequence in which they were persisted. This restores the context
    /// needed to continue an AG-UI conversation while the browser sends only the current turn or tool continuation.
    ///
    /// The query is constrained to the caller's tenant, user, and AG-UI conversation partition. It excludes the
    /// provider's sequence-counter document and returns only each serialized <see cref="ChatMessage"/> payload,
    /// which avoids materializing Cosmos metadata during history replay.
    /// </remarks>
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

            var incomingMessages = FilterReplayableMessages(context.RequestMessages);
            var overlapCount = GetOverlappingMessageCount(messages, incomingMessages);
            var historyToPrepend = messages.Take(messages.Count - overlapCount).ToList();

            Logger.LogInformation(
                "[Transcript] Replaying {MessageCount} of {PersistedMessageCount} messages for conversation {ConversationId}; overlapping incoming messages={OverlapCount}; replay={Messages}; incoming={IncomingMessages}",
                historyToPrepend.Count,
                messages.Count,
                scope.ConversationId,
                overlapCount,
                DescribeMessages(historyToPrepend),
                DescribeMessages(incomingMessages));

            return historyToPrepend;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to load the AG-UI transcript.");
            throw;
        }
    }

    /// <summary>
    /// Persists the replayable request and response messages from a completed Agent Framework invocation.
    /// </summary>
    /// <remarks>
    /// A browser-owned tool result reaches the server through a later AG-UI continuation. The request filter
    /// normalizes that result to a <see cref="ChatRole.Tool"/> message so it survives reload and is available to
    /// later model invocations. Function calls and normal assistant responses are stored from the response path.
    /// </remarks>
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
                    Logger.LogDebug(
                        "[Transcript] Persisted {TranscriptRecordId} at sequence {Sequence} for conversation {ConversationId}: {Message}",
                        key,
                        sequence,
                        scope.ConversationId,
                        DescribeMessage(message));
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

    /// <summary>
    /// Allocates the next replay-order value within one Cosmos conversation partition.
    /// </summary>
    /// <remarks>
    /// The counter is patched on the normal path. A missing counter is expected only for the first persisted
    /// message in a conversation; the conflict path handles two simultaneous first writes safely.
    /// </remarks>
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

    /// <summary>
    /// Resolves the Cosmos partition from the AG-UI thread recorded in the agent session and the current caller.
    /// </summary>
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

    /// <summary>
    /// Selects new inbound AG-UI content that must be persisted before the next model response.
    /// </summary>
    /// <remarks>
    /// AG-UI continuations can include prior messages. The final identified user message is the new user turn.
    /// Browser tool calls and results can arrive independently of that user message, so both sides of every
    /// function interaction are retained for durable, valid replay after a browser reload.
    /// </remarks>
    private static List<ChatMessage> FilterRequestMessages(IEnumerable<ChatMessage> messages)
    {
        var identifiedUserMessage = messages.LastOrDefault(message =>
            message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.MessageId));

        List<ChatMessage> messagesToPersist = [];
        if (identifiedUserMessage is not null)
        {
            messagesToPersist.Add(identifiedUserMessage);
        }

        foreach (var message in messages)
        {
            if (message.Contents.OfType<FunctionCallContent>().Any(call => !string.IsNullOrWhiteSpace(call.CallId)))
            {
                messagesToPersist.Add(message);
            }

            // Browser-owned tools return through a later AG-UI continuation. Store their result as a Tool message
            // even if the transport representation supplied a different role.
            messagesToPersist.AddRange(message.Contents
                .OfType<FunctionResultContent>()
                .Where(result => !string.IsNullOrWhiteSpace(result.CallId))
                .Select(result => new ChatMessage(ChatRole.Tool, [result])));
        }

        return messagesToPersist;
    }

    /// <summary>
    /// Removes protocol-only messages that are not needed to reconstruct later model context.
    /// </summary>
    private static List<ChatMessage> FilterReplayableMessages(IEnumerable<ChatMessage> messages) =>
        [.. messages.Where(IsReplayableTranscriptMessage)];

    private static bool IsReplayableTranscriptMessage(ChatMessage message) =>
        !string.IsNullOrWhiteSpace(message.Text) ||
        message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent);

    /// <summary>
    /// Finds the longest persisted transcript suffix already represented at the beginning of an AG-UI continuation.
    /// </summary>
    /// <remarks>
    /// AG-UI resends the active tool-loop protocol sequence, while this provider owns older durable history. Returning
    /// only the non-overlapping Cosmos prefix keeps persistence transparent to the model: it receives one continuous
    /// transcript rather than the same user/tool interaction from both sources.
    /// </remarks>
    private static int GetOverlappingMessageCount(
        IReadOnlyList<ChatMessage> persistedMessages,
        IReadOnlyList<ChatMessage> incomingMessages)
    {
        var maximumOverlap = Math.Min(persistedMessages.Count, incomingMessages.Count);
        for (var overlapCount = maximumOverlap; overlapCount > 0; overlapCount--)
        {
            var persistedStart = persistedMessages.Count - overlapCount;
            var matches = true;
            for (var index = 0; index < overlapCount; index++)
            {
                if (!HaveSameReplayIdentity(persistedMessages[persistedStart + index], incomingMessages[index]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return overlapCount;
            }
        }

        return 0;
    }

    private static bool HaveSameReplayIdentity(ChatMessage persisted, ChatMessage incoming)
    {
        var persistedIdentity = GetReplayIdentity(persisted);
        var incomingIdentity = GetReplayIdentity(incoming);
        return persistedIdentity is not null && StringComparer.Ordinal.Equals(persistedIdentity, incomingIdentity);
    }

    private static string? GetReplayIdentity(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return $"message:{message.Role.Value}:{message.MessageId}";
        }

        var functionCallIds = message.Contents
            .OfType<FunctionCallContent>()
            .Select(call => call.CallId)
            .Where(callId => !string.IsNullOrWhiteSpace(callId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (functionCallIds.Length > 0)
        {
            return $"function-call:{string.Join(':', functionCallIds)}";
        }

        var functionResultIds = message.Contents
            .OfType<FunctionResultContent>()
            .Select(result => result.CallId)
            .Where(callId => !string.IsNullOrWhiteSpace(callId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return functionResultIds.Length > 0
            ? $"function-result:{string.Join(':', functionResultIds)}"
            : null;
    }

    /// <summary>
    /// Produces a compact, payload-free transcript description for correlating AG-UI continuations with Cosmos replay.
    /// </summary>
    private static string DescribeMessages(IEnumerable<ChatMessage> messages) =>
        string.Join(", ", messages.Select(DescribeMessage));

    private static string DescribeMessage(ChatMessage message)
    {
        var callIds = message.Contents
            .OfType<FunctionCallContent>()
            .Select(call => $"call:{call.Name}:{call.CallId}");
        var resultIds = message.Contents
            .OfType<FunctionResultContent>()
            .Select(result => $"result:{result.CallId}");
        var identifiers = string.Join("|", callIds.Concat(resultIds));

        return $"{message.Role}:messageId={message.MessageId ?? "<none>"}" +
            (string.IsNullOrEmpty(identifiers) ? string.Empty : $":{identifiers}");
    }

    /// <summary>
    /// Builds a deterministic Cosmos document ID for a replayable transcript message.
    /// </summary>
    /// <remarks>
    /// Stable AG-UI user message IDs and function call IDs make continuation retries idempotent. For ordinary
    /// response messages without their own ID, the current user message or tool result anchors the record to a
    /// single turn. The content hash is a last-resort fallback when AG-UI provides neither kind of identifier.
    /// </remarks>
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

    /// <summary>
    /// Gets the stable user-message or tool-result identifier that scopes response-only records to an AG-UI turn.
    /// </summary>
    private static string? GetTurnAnchor(IEnumerable<ChatMessage> requestMessages) =>
        requestMessages.LastOrDefault(message =>
            message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.MessageId))?.MessageId
        ?? requestMessages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .LastOrDefault(result => !string.IsNullOrWhiteSpace(result.CallId))?.CallId;

    /// <summary>
    /// Represents the hierarchical Cosmos partition that isolates one user's AG-UI conversation.
    /// </summary>
    private sealed record ConversationScope(string TenantId, string UserId, string ConversationId)
    {
        public PartitionKey PartitionKey => new PartitionKeyBuilder()
            .Add(TenantId)
            .Add(UserId)
            .Add(ConversationId)
            .Build();
    }

    private sealed record PersistedMessage(ChatMessage Message, int Index);

    /// <summary>
    /// Cosmos document for one replayable message. Newtonsoft attributes match Cosmos's required lowercase
    /// <c>id</c> and configured partition-key property names; the message itself remains System.Text.Json text
    /// because it contains polymorphic Agent Framework content.
    /// </summary>
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

    /// <summary>
    /// Cosmos metadata document that atomically assigns replay order within a conversation partition.
    /// </summary>
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
