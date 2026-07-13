// Copyright (c) Microsoft. All rights reserved.

using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using SimpleAgent;

var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS")))
{
    builder.WebHost.UseUrls($"http://+:{port}", $"https://+:{port}");
}

builder.AddServiceDefaults();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);

Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

TokenCredential credential = new ChainedTokenCredential(
    new DevTemporaryTokenCredential(),
    new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeManagedIdentityCredential = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase)
    }));

string agentName = "agent-dotnet";
var projectClient = new AIProjectClient(foundrySettings.ProjectUri, foundrySettings.GetCredential(builder.Environment));

AIAgent agent = projectClient
    .AsAIAgent(
        model: foundrySettings.DeploymentName,
        name: agentName,
        instructions: """
            You are a helpful AI assistant hosted as a Foundry Hosted Agent.
            You can help with a wide range of tasks including answering questions,
            providing explanations, brainstorming ideas, and offering guidance.
            Be concise, clear, and helpful in your responses.
            """);

builder.Services.AddFoundryResponses(agent);

builder.Services.AddOpenAIConversations();

//builder.Services.AddOpenAIResponses();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDevTemporaryLocalContributorSetup();
}

//builder.Services.AddOpenAIResponses();
// Do I need this locally to mimic what foundry will do when published?
//builder.Services.AddOpenAIConversations();

builder.Services.AddAGUI();

var app = builder.Build();

// Enable CORS
app.UseCors();

// Work around hosted storage conflicts caused by replayed platform response IDs.
app.Use(UseSdkGeneratedResponseIdsForResponses);

//app.UseFoundryLocalUserIdFallback();

// Map Foundry Responses API endpoint at /responses.
app.MapFoundryResponses();
app.MapDevTemporaryLocalAgentEndpoint();

app.MapGet("/liveness", () => Results.Ok("Healthy"));
//app.MapGet("/readiness", () => Results.Ok("Ready")); // Doesn't MapFoundryResponses do this already?

//app.MapOpenAIResponses();

// Lets try mapping the conversations endpoint to see if we can get a thread going...
//app.MapOpenAIConversations();

app.MapAGUI("/ag-ui", agent);

app.Run();

const string AgentResponseIdHeader = "x-agent-response-id";

static async Task UseSdkGeneratedResponseIdsForResponses(HttpContext context, Func<Task> next)
{
    if (HttpMethods.IsPost(context.Request.Method)
        && IsFoundryResponsesPath(context.Request.Path.Value)
        && context.Request.Headers.ContainsKey(AgentResponseIdHeader))
    {
        context.Request.Headers.Remove(AgentResponseIdHeader);
    }

    await next();
}

static bool IsFoundryResponsesPath(string? path)
    => string.Equals(path, "/responses", StringComparison.OrdinalIgnoreCase)
       || (path?.EndsWith("/endpoint/protocols/openai/responses", StringComparison.OrdinalIgnoreCase) ?? false);

sealed class DevTemporaryTokenCredential : TokenCredential
{
    private const string EnvironmentVariable = "AZURE_BEARER_TOKEN";
    private readonly string? token = Environment.GetEnvironmentVariable(EnvironmentVariable);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetAccessToken();

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(GetAccessToken());

    private AccessToken GetAccessToken()
    {
        if (string.IsNullOrWhiteSpace(token) || string.Equals(token, nameof(DefaultAzureCredential), StringComparison.Ordinal))
        {
            throw new CredentialUnavailableException($"{EnvironmentVariable} environment variable is not set.");
        }

        return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
    }
}