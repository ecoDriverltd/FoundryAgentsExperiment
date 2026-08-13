using Aspire.Hosting.Foundry;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;
using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// "Foundry User" (role id 53ca6127-db72-4b80-b1b0-d745d6d5456d) has no named member in
// CognitiveServicesBuiltInRole yet - the struct has an implicit string conversion, so the raw
// role GUID can be passed directly until the SDK adds a friendly name for it.
var foundryUserRole = (CognitiveServicesBuiltInRole)"53ca6127-db72-4b80-b1b0-d745d6d5456d";

var foundry = builder.AddFoundry("foundrysw");

foundry.ConfigureInfrastructure(infra =>
{
    var resources = infra.GetProvisionableResources();
    var foundryResource = resources.OfType<CognitiveServicesAccount>().Single(r => r.BicepIdentifier == foundry.Resource.Name);

    // Setting to Sweden central as default location for OpenAI resources, as this has the greatest model/functionality availability.
    foundryResource.Location = new BicepValue<AzureLocation>(AzureLocation.SwedenCentral);
});

var project = foundry.AddProject("agent-test-sw")
    // The Foundry Memory service authenticates as the *project's own* system-assigned managed
    // identity when it calls back into the embedding deployment (this is a real Entra hop, not
    // implicit same-account access) - grant that identity access to the account. Note:
    // WithRoleAssignments does NOT work for the project's own identity, because Aspire's role
    // assignment builder only wires up role assignments for resources that reference the target
    // via environment variables/args (containers, ProjectResource, etc.) - the Foundry project
    // resource itself is never a valid *source* for WithRoleAssignments, so it's silently a no-op.
    // Instead, hook into ConfigureInfrastructure (which Aspire combines with its own internal
    // callback) to grab the already-provisioned project's managed identity and create the role
    // assignment directly against the parent Foundry account using the typed Bicep CDK.
    .ConfigureInfrastructure(infra =>
    {
        var account = (CognitiveServicesAccount)foundry.Resource.AddAsExistingResource(infra);
        var cogProject = infra.GetProvisionableResources().OfType<CognitiveServicesProject>().Single();

        cogProject.Location = new BicepValue<AzureLocation>(AzureLocation.SwedenCentral);

        // Build the RoleAssignment manually (rather than via CreateRoleAssignment) because that
        // helper derives its Bicep identifier from CognitiveServicesBuiltInRole.GetBuiltInRoleName,
        // which falls back to the raw role GUID for roles without a named member - and GUIDs
        // contain hyphens, which aren't valid in Bicep identifiers.
        var roleAssignment = new RoleAssignment("foundry_project_foundry_user_role")
        {
            Name = BicepFunction.CreateGuid(account.Id, cogProject.Id, foundryUserRole.ToString()),
            Scope = new IdentifierExpression(account.BicepIdentifier),
            PrincipalType = RoleManagementPrincipalType.ServicePrincipal,
            PrincipalId = cogProject.Identity.PrincipalId,
            RoleDefinitionId = BicepFunction.GetSubscriptionResourceId("Microsoft.Authorization/roleDefinitions", foundryUserRole.ToString())
        };
        infra.Add(roleAssignment);
    });

var chat = foundry.AddDeployment("chat-model", FoundryModel.OpenAI.Gpt54Nano);
chat.Resource.SkuCapacity = 60000;

var embeddings = foundry.AddDeployment("embeddings-model", FoundryModel.OpenAI.TextEmbedding3Small);
embeddings.Resource.SkuCapacity = 1000;

// Real Azure Cosmos account (no RunAsEmulator()) - consistent with AddFoundry above, which always
// provisions a real Azure resource even for local F5 runs. Backs the agent's client-managed chat
// history (Microsoft.Agents.AI.CosmosNoSql's CosmosChatHistoryProvider) and the conversation index
// used to list a user's past conversations.
var cosmosDb = builder.AddAzureCosmosDB("cosmos");
var database = cosmosDb.AddCosmosDatabase("agent-history");
// Partition key path must match CosmosChatHistoryProvider.BuildPartitionKey's hierarchical key
// exactly: tenantId -> userId -> conversationId (3 levels), when both tenantId and userId are set.
database.AddContainer("chat-history", partitionKeyPaths: ["/tenantId", "/userId", "/conversationId"]);
// CosmosConversationIndexStore partitions by userId only (see RecordConversationTurnAsync/ListConversationsAsync).
database.AddContainer("conversation-index", partitionKeyPath: "/userId");
// CosmosAgentSessionStore partitions by userId only (see AgentSessionDbContext), storing the small
// serialized AgentSession (ConversationId + StateBag bookkeeping) - never chat messages, which stay
// in the "chat-history" container above.
database.AddContainer("agent-sessions", partitionKeyPath: "/userId");

// Register project as foundry hosted agent
var agent = builder.AddProject<FoundryAgentsExperiment_Agent>("agent-dotnet")
    .WithHttpsEndpoint(targetPort: 9000)
    .WithHttpEndpoint(targetPort: 9001)
    //.WithExternalHttpEndpoints() // Not accessed externally, agent called interally via webhost to keep secure
    .WithReference(foundry)
    .WithReference(project).WaitFor(project)
    .WithReference(chat).WaitFor(chat)
    .WithReference(embeddings).WaitFor(embeddings)
    .WithReference(cosmosDb).WaitFor(cosmosDb)
    .WithReference(database).WaitFor(database)
    .WithRoleAssignments(foundry, CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser)
    .WithEnvironment("MODEL_DEPLOYMENT_NAME", FoundryModel.OpenAI.Gpt54Nano.Name)
    .AsHostedAgent(project,
        configure =>
        {
            configure.ContainerProtocolVersions.Add(new ProtocolVersionRecord(ProjectsAgentProtocol.Responses, "2.0.0"));
            configure.ContainerProtocolVersions.Add(new ProtocolVersionRecord(ProjectsAgentProtocol.Invocations, "1.0.0"));
        });

if (builder.Environment.IsDevelopment())
{
    var devui = builder.AddDevUI("devui")
         .WithAgentService(agent)
         .WaitFor(agent);
}

builder.AddProject<FoundryAgentsExperiment_Web>("foundryagentsexperiment-web")
    .WithReference(project)
    .WithReference(agent).WaitFor(agent)
    .WithExplicitStart();

builder.Build().Run();
