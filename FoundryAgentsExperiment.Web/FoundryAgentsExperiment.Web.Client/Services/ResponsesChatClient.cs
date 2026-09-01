using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FoundryAgentsExperiment.Web.Client.Services;

public sealed record ResponsesStreamUpdate(string Type, JsonElement Data);

public sealed class ResponsesChatClient(HttpClient httpClient)
{
    private readonly HttpClient httpClient = httpClient;

    public Task<string> CreateConversationAsync(CancellationToken cancellationToken) =>
        Task.FromResult($"session_{Guid.NewGuid():N}");

    public async IAsyncEnumerable<ResponsesStreamUpdate> StreamResponseAsync(
        string conversationId,
        string userText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new
        {
            agent_session_id = conversationId,
            input = new[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = userText,
                        },
                    },
                },
            },
            stream = true,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent.Create(request),
        };
        using var response = await this.httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? eventType = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line[7..];
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal) || eventType is null)
                continue;

            using var document = JsonDocument.Parse(line[6..]);
            yield return new ResponsesStreamUpdate(eventType, document.RootElement.Clone());
            eventType = null;
        }
    }
}
