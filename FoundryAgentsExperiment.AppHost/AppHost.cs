using Aspire.Hosting.Foundry;
using Azure.AI.Projects.Agents;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddFoundry("foundry");
var project = foundry.AddProject("agent-test");
var chat = foundry.AddDeployment("chat-model", FoundryModel.OpenAI.Gpt54Nano);
chat.Resource.SkuCapacity = 60000;

var embeddings = foundry.AddDeployment("embeddings-model", FoundryModel.OpenAI.TextEmbedding3Small);
embeddings.Resource.SkuCapacity = 1000;

// Register project as foundry hosted agent
var agent = builder.AddProject<FoundryAgentsExperiment_Agent>("agent-dotnet")
    .WithHttpsEndpoint(targetPort: 9000)
    .WithHttpEndpoint(targetPort: 9001)
    //.WithExternalHttpEndpoints() // Not accessed externally, agent called interally via webhost to keep secure
    .WithReference(foundry)
    .WithReference(project).WaitFor(project)
    .WithReference(chat).WaitFor(chat)
    .WithReference(embeddings).WaitFor(embeddings)
    .WithEnvironment("MODEL_DEPLOYMENT_NAME", FoundryModel.OpenAI.Gpt54Nano.Name)
    .AsHostedAgent(project,
        configure =>
        {
            configure.ContainerProtocolVersions.Add(new ProtocolVersionRecord(ProjectsAgentProtocol.Responses, "2.0.0"));
            configure.ContainerProtocolVersions.Add(new ProtocolVersionRecord(ProjectsAgentProtocol.Invocations, "1.0.0"));
        });

var devui = builder.AddDevUI("devui")
    .WithAgentService(agent)
    .WaitFor(agent);

builder.AddProject<FoundryAgentsExperiment_Web>("foundryagentsexperiment-web")
    .WithReference(project)
    .WithReference(agent).WaitFor(agent)
    .WithExplicitStart();

builder.Build().Run();
