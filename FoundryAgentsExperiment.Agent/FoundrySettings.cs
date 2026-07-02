// Copyright (c) Microsoft. All rights reserved.

using Azure.Core;
using Azure.Identity;
using System.Data.Common;

namespace SimpleAgent;

internal sealed record FoundrySettings(Uri ProjectUri, string DeploymentName)
{
    internal static FoundrySettings FromConfiguration(IConfiguration configuration)
    {
        string projectEndpoint = ParseConnectionValue(
            configuration.GetConnectionString("agent-test")
                ?? throw new InvalidOperationException("Connection string 'agent-test' is not set."),
            "Endpoint");

        string deploymentName = ParseConnectionValue(
            configuration.GetConnectionString("chat-model")
                ?? throw new InvalidOperationException("Connection string 'chat-model' is not set."),
            "Deployment");

        if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out Uri? projectUri))
            throw new InvalidOperationException($"'agent-test' has an invalid Endpoint: '{projectEndpoint}'");

        return new(projectUri, deploymentName);
    }

    // DefaultAzureCredential on a local machine tries ManagedIdentityCredential (IMDS at
    // 169.254.169.254) which can block for the full HttpClient.Timeout. Use a fast chain locally.
    internal TokenCredential GetCredential(IHostEnvironment environment) =>
        environment.IsDevelopment()
            ? new ChainedTokenCredential(
                new VisualStudioCredential(),
                new VisualStudioCodeCredential())
            : new DefaultAzureCredential();

    private static string ParseConnectionValue(string connectionString, string key)
    {
        DbConnectionStringBuilder csb = new() { ConnectionString = connectionString };
        var value = csb.TryGetValue(key, out object? raw) ? raw?.ToString() : null;

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Connection string is missing or has an empty '{key}' value.")
            : value!;
    }
}

internal static class FoundryApplicationExtensions
{
    // The Foundry platform normally injects x-agent-user-id on every inbound request.
    // Locally this header is absent, causing HostedSessionIsolationKeyProvider to throw.
    internal static WebApplication UseFoundryLocalUserIdFallback(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
            app.Use(async (context, next) =>
            {
                if (!context.Request.Headers.ContainsKey("x-agent-user-id"))
                    context.Request.Headers.Append("x-agent-user-id", "local-dev-user");
                await next();
            });

        return app;
    }
}
