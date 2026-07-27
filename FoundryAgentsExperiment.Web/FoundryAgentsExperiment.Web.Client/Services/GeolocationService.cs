using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace FoundryAgentsExperiment.Web.Client.Services;

/// <summary>
/// Wraps the browser's Geolocation API so it can be exposed to the AG-UI agent as a frontend
/// tool (see <c>ChatPage.razor</c>). Scoped because it depends on <see cref="IJSRuntime"/>, which
/// is itself scoped (tied to the WASM circuit / Interactive Server SignalR connection).
/// </summary>
public class GeolocationService(IJSRuntime js)
{
    private const string ModulePath = "./js/geolocation.js";

    private sealed record Coordinates(
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude);

    /// <summary>
    /// Gets the user's current location from the browser's GPS/geolocation API, formatted as a
    /// human-readable latitude/longitude string. Returns an explanatory message instead of
    /// throwing if the browser denies or doesn't support geolocation, since this is called by the
    /// agent as a tool result rather than by application code expecting an exception.
    /// </summary>
    public async Task<string> GetUserLocationAsync()
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            var coordinates = await module.InvokeAsync<Coordinates>("getCurrentPosition");
            return $"{coordinates.Latitude:F4}°N, {coordinates.Longitude:F4}°E";
        }
        catch (JSException ex)
        {
            return $"Unable to determine the user's location: {ex.Message}";
        }
    }
}
