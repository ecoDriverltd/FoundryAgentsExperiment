using Aspire.Hosting.Foundry;
using Azure.AI.Projects.Agents;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddFoundry("foundry");
var project = foundry.AddProject("agent-test");
var chat = foundry.AddDeployment("chat-model", FoundryModel.OpenAI.Gpt54Nano);
chat.Resource.SkuCapacity = 60000;

// Register project as foundry hosted agent
var agent = builder.AddProject<FoundryAgentsExperiment_Agent>("agent-dotnet")
    .WithHttpsEndpoint(targetPort: 9000)
    .WithHttpEndpoint(targetPort: 9001)
    .WithExternalHttpEndpoints()
    .WithReference(project).WaitFor(project)
    .WithReference(chat).WaitFor(chat)
    .WithEnvironment("MODEL_DEPLOYMENT_NAME", FoundryModel.OpenAI.Gpt54Nano.Name)
    .AsHostedAgent(project, configure => configure.ContainerProtocolVersions.Add(new ProtocolVersionRecord("responses", "2.0.0")));

var devui = builder.AddDevUI("devui")
    .WithAgentService(agent)
    .WaitFor(agent);

builder.AddProject<FoundryAgentsExperiment_Web>("foundryagentsexperiment-web")
    .WithReference(project)
    .WithReference(agent).WaitFor(agent)
    .WithExplicitStart();

builder.Build().Run();
