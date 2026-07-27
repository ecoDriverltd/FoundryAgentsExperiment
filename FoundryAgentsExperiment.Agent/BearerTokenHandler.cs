using Azure.Core;
using System.Net.Http.Headers;

namespace FoundryAgentsExperiment.Agent;

// HttpClientHandler that attaches a Foundry bearer token to every outgoing request, caching it
// until shortly before it expires. Without caching, every single MCP request (skill/tool listing,
// tool invocation, etc.) re-triggers a fresh credential.GetTokenAsync call. VisualStudioCredential
// in particular doesn't cache internally - it shells out to the VS auth broker each time - so
// re-fetching per request is slow and can lead to timeouts/cancellation mid-stream.
internal sealed class BearerTokenHandler(TokenCredential credential, string scope) : HttpClientHandler
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly TokenRequestContext _tokenContext = new([scope]);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken? _cachedToken;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AccessToken token = await GetCachedTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccessToken> GetCachedTokenAsync(CancellationToken cancellationToken)
    {
        AccessToken? cached = this._cachedToken;
        if (cached is { } token && token.ExpiresOn - RefreshBuffer > DateTimeOffset.UtcNow)
        {
            return token;
        }

        await this._tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = this._cachedToken;
            if (cached is { } lockedToken && lockedToken.ExpiresOn - RefreshBuffer > DateTimeOffset.UtcNow)
            {
                return lockedToken;
            }

            AccessToken freshToken = await credential.GetTokenAsync(this._tokenContext, cancellationToken).ConfigureAwait(false);
            this._cachedToken = freshToken;
            return freshToken;
        }
        finally
        {
            this._tokenLock.Release();
        }
    }
}
