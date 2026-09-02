using Azure.AI.Projects;
using Azure.Core;
using Azure.Core.Diagnostics;
using FoundryAgentsExperiment.Agent.AgentExtensions;
using FoundryAgentsExperiment.Agent.AgentServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using System.Diagnostics.Tracing;

var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var builder = WebApplication.CreateBuilder(args);

var foundrySettings = FoundrySettings.FromConfiguration(builder.Configuration);
TokenCredential credential = FoundrySettings.GetCredential(builder.Environment);

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("URLS")))
{
    builder.WebHost.UseUrls($"http://+:{port}", $"https://+:{port}");
}

// OLTP errors with this and AgentHostBuilder, so using WebApplicationBuilder instead.
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.AddServiceDefaults();

builder.Services.AddHttpClient().AddLogging();

Console.WriteLine($"Project Endpoint: {foundrySettings.ProjectUri}");
Console.WriteLine($"Model Deployment: {foundrySettings.DeploymentName}");

const string agentName = "agent-dotnet";
var projectClient = new AIProjectClient(foundrySettings.ProjectUri, credential);
var openAIClient = projectClient.GetProjectOpenAIClient();
var modelRequestLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
var modelRequestLogger = modelRequestLoggerFactory.CreateLogger("ModelRequestLogging");
using var azureSdkDiagnostics = AzureEventSourceListener.CreateConsoleLogger(EventLevel.Informational);

// Shared instance so the memory provider resolves the current request context.
var httpContextAccessor = new HttpContextAccessor();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);

// Foundry-managed memory: FoundryMemoryProvider is an AIContextProvider, so it plugs straight into
// ChatClientAgentOptions.AIContextProviders. Foundry runs the chat/embedding models for extraction and
// search itself - we only need to name the deployments, not wire up an embedding client ourselves.
// Memories are scoped per caller using the x-agent-user-id header convention.
var memoryProvider = await projectClient.GetFoundryMemoryProviderAsync(agentName, httpContextAccessor, foundrySettings);
var timingMemoryProvider = new TimingAIContextProvider(
    "FoundryMemoryProvider",
    memoryProvider,
    modelRequestLoggerFactory.CreateLogger<TimingAIContextProvider>());

// Create a POC toolbox/skill/mcp skill provider for the agent to use. The Foundry MCP server handles skill invocation and approval.
// NOTE: this HttpClient must live for the app's lifetime (skillsProvider/agent use it per request via
// BearerTokenHandler), so it's registered with DI for shutdown-time disposal instead of `using var` -
// a `using var` here disposes it right after startup, cancelling every subsequent MCP request.
string toolboxName = "my-toolbox";
var skillsProvider = await builder.GetTestAgentSkillsProviderAsync(projectClient, toolboxName, foundrySettings, credential);

// The default stored Responses output keeps conversation history service-managed.
var modelChatClient = new ModelRequestLoggingChatClient(projectClient
    .GetProjectOpenAIClient()
    .GetProjectResponsesClient()
    .AsIChatClient(), modelRequestLogger);

AIAgent agent = modelChatClient
    .AsBuilder()
    //.UseAIContextProviders([timingMemoryProvider])
    .BuildAIAgent(new ChatClientAgentOptions
    {
        ChatOptions = new()
        {
            Instructions = """
                You are providing general assistance to a user planning their day.
                """,
            ModelId = foundrySettings.DeploymentName,
            Tools = [
                AIFunctionFactory.Create(() => DateTimeOffset.UtcNow,
                    name: "get_current_date_time",
                    description: "Get the current UTC date and time."
            )]
        },
        Name = agentName,
        //AIContextProviders = [skillsProvider]
    });

builder.Services.AddFoundryResponses(agent);

builder.Services.AddFoundryToolboxes(credential);
builder.Services.AddOpenAIConversations();

var agentHost = builder.Build();

agentHost.MapFoundryResponses("/v1");
agentHost.MapOpenAIConversations();

await agentHost.RunAsync();