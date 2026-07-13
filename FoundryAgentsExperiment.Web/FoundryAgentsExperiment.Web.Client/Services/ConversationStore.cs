using System.Text.Json;
using FoundryAgentsExperiment.Web.Client.Models;
using Microsoft.JSInterop;

namespace FoundryAgentsExperiment.Web.Client.Services;

public class ConversationStore(IJSRuntime js)
{
    private const string Key = "conversations";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<ConversationSummary>> GetAllAsync()
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", Key);
        if (json is null)
            return [];

        return JsonSerializer.Deserialize<List<ConversationSummary>>(json, JsonOptions) ?? [];
    }

    public async Task SaveAsync(ConversationSummary conversation)
    {
        var all = await GetAllAsync();
        var index = all.FindIndex(c => c.Id == conversation.Id);
        if (index >= 0)
            all[index] = conversation;
        else
            all.Insert(0, conversation); // newest first

        await js.InvokeVoidAsync("localStorage.setItem", Key, JsonSerializer.Serialize(all, JsonOptions));
    }

    public async Task DeleteAsync(string conversationId)
    {
        var all = await GetAllAsync();
        all.RemoveAll(c => c.Id == conversationId);
        await js.InvokeVoidAsync("localStorage.setItem", Key, JsonSerializer.Serialize(all, JsonOptions));
    }
}
