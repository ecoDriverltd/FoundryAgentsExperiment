using System.Net.Http.Json;
using FoundryAgentsExperiment.Shared.Models;

namespace FoundryAgentsExperiment.Web.Client.Services;

public sealed class AgentConversationClient(HttpClient http)
{
    public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<ConversationSummary>>("agent/conversations", cancellationToken) ?? [];

    public async Task<ConversationDetail> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<ConversationDetail>($"agent/conversations/{Uri.EscapeDataString(conversationId)}", cancellationToken)
            ?? throw new InvalidOperationException("The agent returned an empty conversation response.");

    public async Task UpdateContinuationAsync(
        string conversationId,
        string runId,
        string? initialUserPrompt,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync(
            $"agent/conversations/{Uri.EscapeDataString(conversationId)}/continuation",
            new UpdateConversationContinuationRequest(runId, initialUserPrompt),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
