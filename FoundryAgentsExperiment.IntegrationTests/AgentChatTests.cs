using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Azure.Core;
using Azure.Identity;
using FoundryAgentsExperiment.Shared.Models;
using Microsoft.Azure.Cosmos;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FoundryAgentsExperiment.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AgentChatTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string TestUserIdPrefix = "integration-test-";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private readonly ITestOutputHelper output = output;
    private readonly ConcurrentDictionary<string, byte> testUserIds = new(StringComparer.Ordinal);
    private DistributedApplication? app;
    private string? cosmosConnectionString;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FoundryAgentsExperiment_AppHost>(
            args: [],
            cancellationToken: cancellationToken);

        this.app = await builder.BuildAsync(cancellationToken).WaitAsync(StartupTimeout, cancellationToken);
        await this.app.StartAsync(cancellationToken).WaitAsync(StartupTimeout, cancellationToken);
        await this.app.ResourceNotifications.WaitForResourceHealthyAsync("agent-test-sw", cancellationToken);
        await this.app.ResourceNotifications.WaitForResourceHealthyAsync("agent-dotnet", cancellationToken);
        this.cosmosConnectionString = await this.app.GetConnectionStringAsync("cosmos")
            ?? throw new InvalidOperationException("No Cosmos connection string is available for integration-test cleanup.");
    }

    public async ValueTask DisposeAsync()
    {
        await this.DeleteTestSessionsAsync(TestContext.Current.CancellationToken);
        if (this.app is not null)
            await this.app.DisposeAsync();
    }

    [Fact]
    public async Task ResponsesEndpointStreamsAssistantText()
    {
        var conversation = await this.CreateConversationAsync(TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                agent_session_id = conversation.Id,
                input = "Say hello in exactly one short sentence.",
                stream = true,
            }),
        };
        using var response = await conversation.Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var stream = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        this.output.WriteLine(stream);
        Assert.Contains("event: response.created", stream, StringComparison.Ordinal);
        Assert.Contains("event: response.output_text.delta", stream, StringComparison.Ordinal);
        Assert.Contains("event: response.completed", stream, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesServerToolPersistsConversationWithoutDuplicateHistory()
    {
        var conversation = await this.CreateConversationAsync(TestContext.Current.CancellationToken);
        const string firstPrompt = "Hello.";
        const string toolPrompt = "Use the get_current_date_time tool and tell me the current UTC time.";

        await RunTurnAsync(conversation, firstPrompt, TestContext.Current.CancellationToken);
        var toolResponse = await RunTurnAsync(conversation, toolPrompt, TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(toolResponse));
        var history = await this.ReadPersistedChatHistoryAsync(conversation.Id, TestContext.Current.CancellationToken);
        AssertUserPromptOccursOnce(history, firstPrompt);
        AssertUserPromptOccursOnce(history, toolPrompt);
        Assert.Contains(history, message => HasContentType(message, "functionCall"));
        Assert.Contains(history, message => HasContentType(message, "functionResult"));
    }

    [Fact]
    public async Task ResponsesConversationEndpointsReturnTranscriptAndEnforceOwnership()
    {
        var conversation = await this.CreateConversationAsync(TestContext.Current.CancellationToken);
        var prompt = $"Conversation endpoint integration test {Guid.NewGuid():N}.";
        await RunTurnAsync(conversation, prompt, TestContext.Current.CancellationToken);

        var conversations = await conversation.Http.GetFromJsonAsync<List<ConversationSummary>>(
            "/conversations",
            TestContext.Current.CancellationToken);
        Assert.Contains(conversations!, item => item.Id == conversation.Id);

        var transcript = await conversation.Http.GetFromJsonAsync<ConversationDetail>(
            $"/conversations/{Uri.EscapeDataString(conversation.Id)}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(transcript);
        Assert.Contains(transcript.Messages, message => message.Role == Microsoft.Extensions.AI.ChatRole.User && message.Text == prompt);

        using var otherUserHttp = this.CreateAgentHttpClient(TestUserIdPrefix + Guid.NewGuid().ToString("N"));
        using var otherUserResponse = await otherUserHttp.GetAsync(
            $"/conversations/{Uri.EscapeDataString(conversation.Id)}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherUserResponse.StatusCode);
    }

    private async Task<ResponsesConversation> CreateConversationAsync(CancellationToken cancellationToken)
    {
        var userId = TestUserIdPrefix + Guid.NewGuid().ToString("N");
        var http = this.CreateAgentHttpClient(userId);
        return new ResponsesConversation(http, $"session_{Guid.NewGuid():N}");
    }

    private static async Task<string> RunTurnAsync(ResponsesConversation conversation, string prompt, CancellationToken cancellationToken)
    {
        using var response = await conversation.Http.PostAsJsonAsync("/v1/responses", new
        {
            agent_session_id = conversation.Id,
            input = prompt,
            stream = false,
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return string.Concat(document.RootElement.GetProperty("output")
            .EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "message")
            .SelectMany(item => item.GetProperty("content").EnumerateArray())
            .Where(content => content.TryGetProperty("type", out var type) && type.GetString() == "output_text")
            .Select(content => content.GetProperty("text").GetString()));
    }

    private HttpClient CreateAgentHttpClient(string userId)
    {
        this.testUserIds.TryAdd(userId, 0);
        var http = this.app!.CreateHttpClient("agent-dotnet");
        http.DefaultRequestHeaders.Add("x-agent-user-id", userId);
        return http;
    }

    private async Task DeleteTestSessionsAsync(CancellationToken cancellationToken)
    {
        if (this.cosmosConnectionString is null)
            return;

        using var cosmosClient = new CosmosClient(this.cosmosConnectionString, GetCredential());
        var sessions = cosmosClient.GetContainer("agent-history", "agent-sessions");
        var history = cosmosClient.GetContainer("agent-history", "agent-chat-history");
        foreach (var userId in this.testUserIds.Keys)
        {
            var query = new QueryDefinition("SELECT c.id FROM c WHERE c.userId = @userId").WithParameter("@userId", userId);
            using var iterator = sessions.GetItemQueryIterator<StoredId>(query);
            while (iterator.HasMoreResults)
            {
                foreach (var session in await iterator.ReadNextAsync(cancellationToken))
                {
                    using var historyIterator = history.GetItemQueryIterator<StoredId>(
                        new QueryDefinition("SELECT c.id FROM c WHERE c.conversationId = @conversationId").WithParameter("@conversationId", session.Id),
                        requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(session.Id) });
                    while (historyIterator.HasMoreResults)
                    {
                        foreach (var item in await historyIterator.ReadNextAsync(cancellationToken))
                            await history.DeleteItemAsync<StoredId>(item.Id, new PartitionKey(session.Id), cancellationToken: cancellationToken);
                    }

                    await sessions.DeleteItemAsync<StoredId>(session.Id, new PartitionKey(userId), cancellationToken: cancellationToken);
                }
            }
        }
    }

    private async Task<List<JsonElement>> ReadPersistedChatHistoryAsync(string conversationId, CancellationToken cancellationToken)
    {
        using var cosmosClient = new CosmosClient(this.cosmosConnectionString, GetCredential());
        var container = cosmosClient.GetContainer("agent-history", "agent-chat-history");
        var query = new QueryDefinition("SELECT c.message FROM c WHERE c.conversationId = @conversationId")
            .WithParameter("@conversationId", conversationId);
        using var iterator = container.GetItemQueryIterator<StoredMessage>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(conversationId) });
        var messages = new List<JsonElement>();
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                using var document = JsonDocument.Parse(item.Message);
                messages.Add(document.RootElement.Clone());
            }
        }

        return messages;
    }

    private static void AssertUserPromptOccursOnce(IEnumerable<JsonElement> messages, string prompt) =>
        Assert.Equal(1, messages.Count(message =>
            message.TryGetProperty("role", out var role) && role.GetString() == "user" &&
            message.TryGetProperty("contents", out var contents) &&
            contents.EnumerateArray().Any(content => content.TryGetProperty("text", out var text) && text.GetString() == prompt)));

    private static bool HasContentType(JsonElement message, string type) =>
        message.TryGetProperty("contents", out var contents) &&
        contents.EnumerateArray().Any(content => content.TryGetProperty("$type", out var contentType) && contentType.GetString() == type);

    private sealed record ResponsesConversation(HttpClient Http, string Id);
    private sealed record StoredId(string Id);
    private sealed record StoredMessage(string Message);

    private static TokenCredential GetCredential() => new ChainedTokenCredential(
        new VisualStudioCredential(),
        new VisualStudioCodeCredential(),
        new DefaultAzureCredential());
}
