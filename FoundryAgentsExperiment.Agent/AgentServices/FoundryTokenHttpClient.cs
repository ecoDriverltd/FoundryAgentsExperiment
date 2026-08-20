using Azure.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryAgentsExperiment.Agent.AgentServices;

public static class FoundryTokenHttpClient
{
    public static HttpClient GetClient(TokenCredential credential)
    {
        HttpClient client = new(new BearerTokenHandler(credential, "https://ai.azure.com/.default")
        {
            CheckCertificateRevocationList = true
        });

        return client;
    }

    extension(IHostApplicationBuilder builder)
    {
        public HttpClient AddFoundryTokenHttpClient(TokenCredential credential)
        {
            var client = GetClient(credential);
            builder.Services.TryAddSingleton(client);
            return client;
        }
    }
}
