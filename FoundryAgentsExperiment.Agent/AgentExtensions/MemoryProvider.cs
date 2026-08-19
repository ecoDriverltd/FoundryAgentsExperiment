using Azure.AI.Projects;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;

namespace FoundryAgentsExperiment.Agent.AgentExtensions;

public static class MemoryProvider
{
    extension(AIProjectClient projectClient)
    {
        public async ValueTask<FoundryMemoryProvider> GetFoundryMemoryProviderAsync(string agentName, IHttpContextAccessor httpContextAccessor, FoundrySettings foundrySettings)
        {
            // Foundry-managed memory: FoundryMemoryProvider is an AIContextProvider, so it plugs straight into
            // ChatClientAgentOptions.AIContextProviders. Foundry runs the chat/embedding models for extraction and
            // search itself - we only need to name the deployments, not wire up an embedding client ourselves.
            // Memories are scoped per caller using the same x-agent-user-id header convention as
            // FoundryBackedAgentSessionStore (not the unrelated x-memory-user-id header used by the hosted
            // memory-search *tool* on versioned Foundry agents).
            using var memoryProviderLoggerFactory = LoggerFactory.Create(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Trace);
            });

            var memoryProvider = new FoundryMemoryProvider(
                projectClient,
                options: new FoundryMemoryProviderOptions
                {
                    EnableSensitiveTelemetryData = true,
                    SearchInputMessageFilter = FilterSearchInputMessages,
                    StorageInputRequestMessageFilter = FilterConversationMessages,
                    StorageInputResponseMessageFilter = FilterConversationMessages
                },
                memoryStoreName: $"{agentName}-memory",
                stateInitializer: _ => new FoundryMemoryProvider.State(
                    new FoundryMemoryProviderScope(httpContextAccessor.GetAgentUserId())),
                loggerFactory: memoryProviderLoggerFactory);

            await memoryProvider.EnsureMemoryStoreCreatedAsync(
                chatModel: foundrySettings.DeploymentName,
                embeddingModel: foundrySettings.EmbeddingDeploymentName,
                description: $"Memory store for {agentName}");

            return memoryProvider;
        }
    }

    public static IEnumerable<ChatMessage> FilterSearchInputMessages(IEnumerable<ChatMessage> messages) =>
        messages.Where(message =>
            message.Role == ChatRole.User &&
            IsPlainTextMessage(message));

    public static IEnumerable<ChatMessage> FilterConversationMessages(IEnumerable<ChatMessage> messages) =>
        messages.Where(message =>
            (message.Role == ChatRole.User || message.Role == ChatRole.Assistant) &&
            IsPlainTextMessage(message));

    private static bool IsPlainTextMessage(ChatMessage message) =>
        message.Contents.All(content => content is TextContent) &&
        !string.IsNullOrWhiteSpace(message.Text);
}
