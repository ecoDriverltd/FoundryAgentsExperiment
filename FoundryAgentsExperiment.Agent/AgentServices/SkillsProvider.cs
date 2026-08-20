using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using FoundryAgentsExperiment.Agent.AgentServices;
using Microsoft.Agents.AI;
using ModelContextProtocol.Client;

namespace FoundryAgentsExperiment.Agent.AgentExtensions;

public static class SkillsProvider
{
    extension(IHostApplicationBuilder builder)
    {
        public async ValueTask<AgentSkillsProvider> GetTestAgentSkillsProviderAsync(AIProjectClient projectClient,
             string toolboxName, FoundrySettings foundrySettings, TokenCredential credential)
        {
            var foundryTokenHttpClient = builder.AddFoundryTokenHttpClient(credential);

            AgentAdministrationClient agentAdminClient = projectClient.AgentAdministrationClient;
            AgentToolboxes toolboxClient = agentAdminClient.GetAgentToolboxes();

#pragma warning disable AAIP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            ProjectAgentSkills skillsClient = agentAdminClient.GetAgentSkills();
#pragma warning restore AAIP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

            // For testing, create a skill inline. For the real system we could build a skill management UI.
            SkillVersion created = await skillsClient.CreateSkillVersionAsync("silly-math", inlineContent:
                new SkillInlineContent("A silly math skill for handling all mathmatical operations in a daft way",
                    "Whenever a mathmatical calculation needs to be evaluated, just return '42' as the answer. So if the user asks for 1 + 1, return '42'."));

            ToolboxSkillReference skillRef = new("silly-math");  // exiting skill, add { Version = "v1" } to pin

            ToolboxVersion toolboxVersion = await toolboxClient.CreateVersionAsync(
                name: toolboxName,
                tools: [],
                skills: [skillRef],
                description: "Toolbox with a skill reference");

            string toolboxMcpServerUrl = $"{foundrySettings.ProjectUri.ToString().TrimEnd('/')}/toolboxes/{toolboxName}/mcp?api-version=v1";

            // NOTE: must NOT be disposed here - skillsProvider holds onto this client and uses it for the
            // agent's entire lifetime. `await using` would dispose (and cancel) its transport as soon as
            // this method returns, causing every subsequent MCP call to fail immediately with a
            // TaskCanceledException. Register it with DI instead so the host disposes it at shutdown.
            var mcpClient = await McpClient.CreateAsync(
                new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(toolboxMcpServerUrl),
                        Name = toolboxName,
                        TransportMode = HttpTransportMode.StreamableHttp,
                    },
                    foundryTokenHttpClient));

            builder.Services.AddSingleton(mcpClient);

            // DisableLoadSkillApproval/DisableReadSkillResourceApproval/DisableRunSkillScriptApproval: without
            // these, load_skill (and friends) are human-in-the-loop tools that raise a FunctionApprovalRequest
            // which nobody ever answers over AG-UI/streaming, so the turn just stalls with a pending
            // FunctionCallContent and never produces a response. All skills will be internal/trusted anyway in our case.
            var skillsProvider = new AgentSkillsProviderBuilder()
                .UseMcpSkills(mcpClient)
                .UseOptions(options =>
                {
                    options.DisableLoadSkillApproval = true;
                    options.DisableReadSkillResourceApproval = true;
                    options.DisableRunSkillScriptApproval = true;
                })
                .Build();

            return skillsProvider;
        }
    }
}