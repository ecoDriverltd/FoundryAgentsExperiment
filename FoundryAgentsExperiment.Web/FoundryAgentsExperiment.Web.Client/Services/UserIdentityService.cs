using Microsoft.JSInterop;

namespace FoundryAgentsExperiment.Web.Client.Services;

public class UserIdentityService(IJSRuntime js)
{
    private const string Key = "poc-user-id";
    private string? _userId;

    public async Task<string> GetUserIdAsync()
    {
        if (_userId is not null)
            return _userId;

        var stored = await js.InvokeAsync<string?>("localStorage.getItem", Key);
        if (stored is { Length: > 0 })
        {
            _userId = stored;
            return _userId;
        }

        _userId = Guid.NewGuid().ToString("N")[..8]; // short friendly ID for POC
        await js.InvokeVoidAsync("localStorage.setItem", Key, _userId);
        return _userId;
    }
}
