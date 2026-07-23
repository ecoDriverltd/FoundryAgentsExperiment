//using Azure.AI.Projects;
//using Azure.AI.Projects.Agents;
//using Microsoft.Agents.AI.Foundry;
//using Microsoft.Agents.AI.Foundry.Hosting;
//using SimpleAgent;

//var builder = WebApplication.CreateBuilder(args);

//var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

//if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
//    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS")))
//{
//    builder.WebHost.UseUrls($"http://+:{port}", $"https://+:{port}");
//}

//var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);
//var agentName = "agent-dotnet";

//var aiProjectClient = new AIProjectClient(foundrySettings.ProjectUri, foundrySettings.GetCredential(builder.Environment));

//var agentsClient = aiProjectClient.GetProjectAgentsClient();

//// Need to create the agent if it doesn't exist...

////aiProjectClient.AgentAdministrationClient.cre
//var agentList = await agentsClient.GetAgentsAsync().ToListAsync();

//// Retrieve the Foundry-managed agent by name 
//ProjectsAgentRecord? agentRecord = agentList.FirstOrDefault(a => a.Name == agentName);

//if (agentRecord == null)
//{
//    var agentDefinition = new HostedAgentDefinition(
//        [new(ProjectsAgentProtocol.Responses, "2.0.0")],
//        "0.25",
//        "0.5Gi"
//    );

//    agentDefinition.

//    ProjectsAgentDefinition agentDefinition = new DeclarativeAgentDefinition("gpt-5-mini") // supports all Foundry direct models
//    {
//        Instructions = "You are a helpful assistant that answers general questions",
//    };


//    // Create the agent if it doesn't exist
//    agentsClient.CreateAgentVersionAsync(agentName,
//        new ProjectsAgentVersionCreationOptions(
//            new HostedAgentDefinition([new(ProjectsAgentProtocol.Responses, "2.0.0")], "0.25", "0.5Gi")
//            {

//            })
//}

//await aiProjectClient.AgentAdministrationClient.GetAgentAsync(agentName);

//FoundryAgent agent = aiProjectClient.AsAIAgent(agentRecord);

//// Host the agent as a Foundry Hosted Agent using the Responses API.

//builder.AddServiceDefaults();

//builder.Services.AddFoundryResponses(agent);

//var app = builder.Build();
//app.MapFoundryResponses();

//// Contributor-only: in Development, also map the per-agent OpenAI route shape that live Foundry uses
//// so a local REPL client can target this server via AIProjectClient.AsAIAgent(Uri agentEndpoint).
//// Do not use this in production. Hosted Foundry agents only support the agent-endpoint path.
//app.MapDevTemporaryLocalAgentEndpoint();

//app.Run();