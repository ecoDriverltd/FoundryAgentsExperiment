using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using FoundryAgentsExperiment.SampleParityAgent;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using System.Data.Common;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient().AddLogging();
builder.Services.AddAGUIServer();
builder.Services.AddSingleton<AgentIsolationKeyProvider, SampleParityIsolationKeyProvider>();
builder.Services.AddSingleton<InspectableInMemoryAgentSessionStore>();

var projectEndpoint = GetConnectionValue(builder.Configuration, "agent-test-sw", "Endpoint");
var deploymentName = GetConnectionValue(builder.Configuration, "chat-model", "Deployment");
TokenCredential credential = builder.Environment.IsDevelopment()
    ? new ChainedTokenCredential(new VisualStudioCredential(), new VisualStudioCodeCredential())
    : new DefaultAzureCredential();

var projectClient = new AIProjectClient(new Uri(projectEndpoint), credential);
var chatClient = projectClient
    .GetProjectOpenAIClient()
    .GetProjectResponsesClient()
    .AsIChatClientWithStoredOutputDisabled();

const string agentName = "sample-parity-agent";
const string perServiceAgentName = "sample-parity-per-service-agent";
var agent = CreateAgent(agentName, requirePerServiceCallChatHistoryPersistence: false);
var perServiceAgent = CreateAgent(perServiceAgentName, requirePerServiceCallChatHistoryPersistence: true);

builder
    .AddAIAgent(agentName, (_, _) => agent)
    .WithSessionStore((serviceProvider, _) => serviceProvider.GetRequiredService<InspectableInMemoryAgentSessionStore>());
builder
    .AddAIAgent(perServiceAgentName, (_, _) => perServiceAgent)
    .WithSessionStore((serviceProvider, _) => serviceProvider.GetRequiredService<InspectableInMemoryAgentSessionStore>());

var app = builder.Build();
app.MapGet("/_diagnostics/sessions/{agentId}/{threadId}", (
    string agentId,
    string threadId,
    InspectableInMemoryAgentSessionStore sessionStore) =>
{
    return sessionStore.TryGetSerializedSessionByThreadId(agentId, threadId, out var serializedSession)
        ? Results.Text(serializedSession.GetRawText(), "application/json")
        : Results.Json(new { threadId, storedSessionKeys = sessionStore.GetStoredSessionKeys() }, statusCode: StatusCodes.Status404NotFound);
});
app.MapAGUIServer(perServiceAgentName, "/per-service");
app.MapAGUIServer(agentName, "/");
await app.RunAsync();

ChatClientAgent CreateAgent(string name, bool requirePerServiceCallChatHistoryPersistence) =>
    chatClient
        .AsBuilder()
        .BuildAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            RequirePerServiceCallChatHistoryPersistence = requirePerServiceCallChatHistoryPersistence,
            ChatOptions = new ChatOptions
            {
                ModelId = deploymentName,
                Instructions = "You are a helpful assistant. Use get_current_time whenever the user asks for the current UTC time.",
                Tools =
                [
                    AIFunctionFactory.Create(
                        () => DateTimeOffset.UtcNow,
                        name: "get_current_time",
                        description: "Get the current UTC time.")
                ]
            }
        });

static string GetConnectionValue(IConfiguration configuration, string connectionName, string key)
{
    var connectionString = configuration.GetConnectionString(connectionName)
        ?? throw new InvalidOperationException($"Connection string '{connectionName}' is not set.");
    var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
    return builder.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
        ? text
        : throw new InvalidOperationException($"Connection string '{connectionName}' is missing '{key}'.");
}

sealed class SampleParityIsolationKeyProvider : AgentIsolationKeyProvider
{
    public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<string?>("sample-parity-local-user");
}
