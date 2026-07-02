// Copyright (c) Microsoft. All rights reserved.

using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using SimpleAgent;

var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{port}");
builder.AddServiceDefaults();

var foundry = FoundrySettings.FromConfiguration(builder.Configuration);

Console.WriteLine($"Project Endpoint: {foundry.ProjectUri}");
Console.WriteLine($"Model Deployment: {foundry.DeploymentName}");

AIAgent agent = new AIProjectClient(foundry.ProjectUri, foundry.GetCredential(builder.Environment))
    .AsAIAgent(
        model: foundry.DeploymentName,
        name: "agent-dotnet",
        instructions: """
            You are a helpful AI assistant hosted as a Foundry Hosted Agent.
            You can help with a wide range of tasks including answering questions,
            providing explanations, brainstorming ideas, and offering guidance.
            Be concise, clear, and helpful in your responses.
            """);

builder.Services.AddFoundryResponses(agent);
builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

var app = builder.Build();

app.UseFoundryLocalUserIdFallback();

app.MapFoundryResponses();
app.MapOpenAIResponses();
app.MapOpenAIConversations();

app.MapGet("/health", () => Results.Ok("Healthy"));
app.MapGet("/liveness", () => Results.Ok("Healthy"));
app.MapGet("/readiness", () => Results.Ok("Ready"));

app.Run();