using Aspire.Hosting.Foundry;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddFoundry("foundry");
var project = foundry.AddProject("agent-test");
var chat = foundry.AddDeployment("chat-model", FoundryModel.OpenAI.Gpt54Nano);

// Register project as foundry hosted agent
var agent = builder.AddProject<FoundryAgentsExperiment_Agent>("agent-dotnet")
    .WithHttpEndpoint(targetPort: 9000)
    .WithReference(project).WaitFor(project)
    .WithReference(chat).WaitFor(chat)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("MODEL_DEPLOYMENT_NAME", FoundryModel.OpenAI.Gpt54Nano.Name)
    .AsHostedAgent(project);

var devui = builder.AddDevUI("devui")
    .WithAgentService(agent)
    .WaitFor(agent);

builder.Build().Run();
