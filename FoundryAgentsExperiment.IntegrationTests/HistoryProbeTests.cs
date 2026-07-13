using Aspire.Hosting.Testing;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;
using Projects;
using System.Data.Common;
using System.Text;
using Xunit;

namespace FoundryAgentsExperiment.IntegrationTests;

/// <summary>
/// Probes whether Foundry conversation history is accessible via
/// AIProjectClient.ProjectOpenAIClient.GetProjectConversationsClient().
///
/// Strategy:
///   1. Run two turns via AG-UI to produce a real conversation ID with history.
///   2. Query ProjectConversationsClient directly for that ID.
///   3. Log everything so we can see what comes back.
/// </summary>
[Trait("Category", "Integration")]
public class HistoryProbeTests : IAsyncLifetime
{
    private DistributedApplicationFactory? _factory;
    private readonly ITestOutputHelper _output;

    public HistoryProbeTests(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        _factory = new DistributedApplicationFactory(typeof(FoundryAgentsExperiment_AppHost));
        await _factory.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    private AIAgent CreateAgent(string userId)
    {
        var http = _factory!.CreateHttpClient("agent-dotnet");
        http.DefaultRequestHeaders.Add("x-agent-user-id", userId);
        return new AGUIChatClient(http, "/ag-ui")
            .AsAIAgent(name: "agui-client", description: "AG-UI Client Agent");
    }

    private static async Task<(string text, string? conversationId)> RunTurnAsync(
        AIAgent agent, AgentSession session, IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        string? conversationId = null;

        await foreach (var update in agent.RunStreamingAsync(messages, session))
        {
            var chat = update.AsChatResponseUpdate();
            if (!string.IsNullOrEmpty(chat.ConversationId))
                conversationId = chat.ConversationId;
            foreach (var c in update.Contents)
                if (c is TextContent tc) sb.Append(tc.Text);
        }

        return (sb.ToString(), conversationId);
    }

    /// <summary>
    /// Run two turns via AG-UI, then query ProjectConversationsClient for the
    /// conversation and its items. The test always "fails" so the output is
    /// always printed — read the log to see what each API returns.
    /// </summary>
    [Fact]
    public async Task ProjectConversationsClient_FetchConversationAfterAgUITurns()
    {
        var log = new StringBuilder();
        var userId = "hist-probe-" + Guid.NewGuid().ToString("N");
        var agent = CreateAgent(userId);
        var session = await agent.CreateSessionAsync();

        // Turn 1 — plant a fact
        var (text1, convId) = await RunTurnAsync(agent, session,
        [
            new(ChatRole.System, "You are a helpful assistant. Be concise."),
            new(ChatRole.User, "My secret number is 54321.")
        ]);
        log.AppendLine($"Turn 1: {text1}");
        log.AppendLine($"ConversationId (from update): {convId ?? "(null)"}");

        if (session is ChatClientAgentSession typed)
            log.AppendLine($"ChatClientAgentSession.ConversationId: {typed.ConversationId ?? "(null)"}");

        // Turn 2 — verify AG-UI recall works (sanity)
        var (text2, _) = await RunTurnAsync(agent, session,
        [
            new(ChatRole.System, "You are a helpful assistant. Be concise."),
            new(ChatRole.User, "What secret number did I just give you?")
        ]);
        log.AppendLine($"Turn 2: {text2}");
        log.AppendLine($"AG-UI history recall: {(text2.Contains("54321") ? "PASS ✓" : "FAIL ✗")}");
        log.AppendLine();

        // ── Now query via ProjectConversationsClient ──────────────────────────────────
        var connectionString = await _factory!.GetConnectionString("agent-test");
        log.AppendLine($"agent-test connection string: {connectionString ?? "(null)"}");

        // The connection string is in DbConnectionString format: "Endpoint=https://...;..."
        // Parse the Endpoint value the same way FoundrySettings.FromConfiguration does.
        string? projectUri = null;
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var csb = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
            if (csb.TryGetValue("Endpoint", out var ep))
                projectUri = ep?.ToString();
        }
        log.AppendLine($"Parsed project URI: {projectUri ?? "(null)"}");

        if (string.IsNullOrWhiteSpace(projectUri) || string.IsNullOrWhiteSpace(convId))
        {
            log.AppendLine("SKIP: missing project URI or conversationId — cannot probe.");
        }
        else
        {
            var projectClient = new Azure.AI.Projects.AIProjectClient(
                new Uri(projectUri),
                new ChainedTokenCredential(new VisualStudioCredential(), new VisualStudioCodeCredential()));

            var convClient = projectClient.ProjectOpenAIClient.GetProjectConversationsClient();

            // a) Get conversation metadata
            try
            {
                var result = await convClient.GetProjectConversationAsync(convId);
                var raw = result.GetRawResponse();
                log.AppendLine($"GetProjectConversationAsync -> HTTP {raw.Status}");
                var body = raw.Content.ToString();
                log.AppendLine($"  {body[..Math.Min(600, body.Length)]}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"GetProjectConversationAsync threw {ex.GetType().Name}: {ex.Message[..Math.Min(300, ex.Message.Length)]}");
            }

            log.AppendLine();

            // b) Get conversation items — the actual messages
            try
            {
                var items = new List<AgentResponseItem>();
                await foreach (var item in convClient.GetProjectConversationItemsAsync(convId))
                    items.Add(item);

                log.AppendLine($"GetProjectConversationItemsAsync -> {items.Count} items");
                foreach (var item in items)
                    log.AppendLine($"  [{item.GetType().Name}] id={item.Id}  responseId={item.ResponseId}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"GetProjectConversationItemsAsync threw {ex.GetType().Name}: {ex.Message[..Math.Min(300, ex.Message.Length)]}");
            }
        }

        var logPath = Path.Combine(Path.GetTempPath(), "hist-probe.txt");
        await File.WriteAllTextAsync(logPath, log.ToString());
        _output.WriteLine($"Log: {logPath}");
        _output.WriteLine(log.ToString());

        Assert.Fail("Intentional — read output above to see what the API returns");
    }

    /// <summary>
    /// List ALL project conversations for the "agent-dotnet" agent, then for each one
    /// try to fetch its items. This tells us:
    ///   - whether our AG-UI thread IDs appear anywhere in this list
    ///   - what a valid ProjectConversation ID looks like (vs thread_xxx)
    ///   - what conversation metadata (Id, CreatedAt, Metadata) is stored
    /// </summary>
    [Fact]
    public async Task GetProjectConversationsAsync_ListAllAndInspect()
    {
        var log = new StringBuilder();

        // Run one AG-UI turn first so there's definitely something in the system
        var userId = "list-probe-" + Guid.NewGuid().ToString("N");
        var agent = CreateAgent(userId);
        var session = await agent.CreateSessionAsync();

        var (text, threadId) = await RunTurnAsync(agent, session,
        [
            new(ChatRole.System, "You are a helpful assistant. Be concise."),
            new(ChatRole.User, "Say exactly: probe-complete")
        ]);
        log.AppendLine($"AG-UI turn text: {text}");
        log.AppendLine($"AG-UI thread ID (ChatClientAgentSession.ConversationId): {(session is ChatClientAgentSession s ? s.ConversationId : "(not ChatClientAgentSession)")}");
        log.AppendLine($"AG-UI thread ID (from update ConversationId):             {threadId ?? "(null)"}");
        log.AppendLine();

        // What does this give us?
        var serialized = await agent.SerializeSessionAsync(session);
        var test = serialized.ToString();

        var connectionString = await _factory!.GetConnectionString("agent-test");
        var csb = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        csb.TryGetValue("Endpoint", out var epObj);
        var projectUri = epObj?.ToString();
        log.AppendLine($"Project URI: {projectUri}");

        var projectClient = new Azure.AI.Projects.AIProjectClient(
            new Uri(projectUri!),
            new ChainedTokenCredential(new VisualStudioCredential(), new VisualStudioCodeCredential()));

        var convClient = projectClient.ProjectOpenAIClient.GetProjectConversationsClient();

        // List conversations scoped to our agent
        var agentRef = new AgentReference("agent-dotnet", version: null);

        try
        {
            var conversations = new List<ProjectConversation>();
            await foreach (var conv in convClient.GetProjectConversationsAsync(agentRef))
                conversations.Add(conv);

            log.AppendLine($"Total conversations returned for agent 'agent-dotnet': {conversations.Count}");
            log.AppendLine();

            foreach (var conv in conversations)
            {
                log.AppendLine($"  Conversation ID : {conv.Id}");
                log.AppendLine($"  CreatedAt       : {conv.CreatedAt}");
                if (conv.Metadata?.Count > 0)
                    foreach (var kv in conv.Metadata)
                        log.AppendLine($"  Metadata[{kv.Key}] = {kv.Value}");

                // Does this conversation's ID match our AG-UI thread ID in any way?
                log.AppendLine($"  Matches AG-UI thread ID: {conv.Id == threadId}");

                // Try fetching items for this conversation
                try
                {
                    var items = new List<AgentResponseItem>();
                    await foreach (var item in convClient.GetProjectConversationItemsAsync(conv.Id))
                        items.Add(item);

                    log.AppendLine($"  Items count: {items.Count}");
                    foreach (var item in items.Take(3))
                        log.AppendLine($"    [{item.GetType().Name}] id={item.Id} responseId={item.ResponseId}");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"  GetProjectConversationItemsAsync threw {ex.GetType().Name}: {ex.Message[..Math.Min(200, ex.Message.Length)]}");
                }

                log.AppendLine();
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"GetProjectConversationsAsync threw {ex.GetType().Name}: {ex.Message[..Math.Min(400, ex.Message.Length)]}");
        }

        var logPath = Path.Combine(Path.GetTempPath(), "hist-list.txt");
        await File.WriteAllTextAsync(logPath, log.ToString());
        _output.WriteLine($"Log: {logPath}");
        _output.WriteLine(log.ToString());

        Assert.Fail("Intentional — read output above");
    }

    /// <summary>
    /// Uses AIProjectClient.AsAIAgent() directly (no AG-UI) — service-managed storage path.
    /// Runs two turns, captures ChatClientAgentSession.ConversationId, then queries
    /// ProjectResponsesClient.GetProjectResponsesAsync(..., conversationId) to confirm
    /// the history is retrievable from the Foundry Responses store.
    ///
    /// This validates that SerializeSession/DeserializeSessionAsync is the right resume
    /// mechanism, and that GetProjectResponsesAsync is the right retrieval API.
    /// </summary>
    [Fact]
    public async Task DirectFoundrySdk_NoAgUI_CanRetrieveHistoryViaProjectResponsesClient()
    {
        var log = new StringBuilder();

        // ── 1. Build the connection details ────────────────────────────────────────
        var connectionString = await _factory!.GetConnectionString("agent-test");
        var modelConnectionString = await _factory!.GetConnectionString("chat-model");

        var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
        csb.TryGetValue("Endpoint", out var epObj);
        var projectUri = epObj?.ToString()!;

        var modelCsb = new DbConnectionStringBuilder { ConnectionString = modelConnectionString };
        modelCsb.TryGetValue("Deployment", out var deploymentObj);
        var deploymentName = deploymentObj?.ToString()!;

        log.AppendLine($"Project URI:      {projectUri}");
        log.AppendLine($"Deployment:       {deploymentName}");

        var credential = new ChainedTokenCredential(new VisualStudioCredential(), new VisualStudioCodeCredential());
        var projectClient = new AIProjectClient(new Uri(projectUri), credential);

        // ── 2. Build agent directly from AIProjectClient — service-managed storage ─
        // AsAIAgent(model, name, instructions, description) from Microsoft.Agents.AI.Foundry
        AIAgent agent = projectClient.AsAIAgent(
            deploymentName,
            "direct-sdk-probe",
            "You are a helpful assistant. Be concise.",
            "Direct SDK probe agent");

        var session = await agent.CreateSessionAsync();
        log.AppendLine($"Session type: {session.GetType().Name}");

        // ── 3. Turn 1 — plant a fact ────────────────────────────────────────────────
        var turn1Response = await agent.RunAsync("My secret code is ALPHA-7.", session);
        var turn1Text = turn1Response.ToString();
        log.AppendLine($"Turn 1 response: {turn1Text}");

        var typedSession = session as ChatClientAgentSession;
        var convId = typedSession?.ConversationId;
        log.AppendLine($"ChatClientAgentSession.ConversationId after turn 1: {convId ?? "(null)"}");

        // ── 4. Turn 2 — verify recall ───────────────────────────────────────────────
        var turn2Response = await agent.RunAsync("What secret code did I just give you?", session);
        var turn2Text = turn2Response.ToString();
        log.AppendLine($"Turn 2 response: {turn2Text}");
        log.AppendLine($"AG recall check: {(turn2Text.Contains("ALPHA-7") ? "PASS ✓" : "FAIL ✗")}");
        log.AppendLine();

        // Note: ChatClientAgentSession.Serialize(options) is the documented persistence mechanism.
        // Covered by the docs at /agent-framework/agents/conversations/storage
        log.AppendLine($"ConversationId (for persistence/resume): {convId ?? "(null)"}");
        log.AppendLine();

        // ── 6. Query ProjectResponsesClient with conversationId ─────────────────────
        if (string.IsNullOrWhiteSpace(convId))
        {
            log.AppendLine("SKIP: ConversationId is null — cannot probe responses.");
        }
        else
        {
            var agentRef = new AgentReference("agent-dotnet", version: null);
            var responsesClient = projectClient.ProjectOpenAIClient.GetProjectResponsesClient();

            log.AppendLine($"Calling GetProjectResponsesAsync(agentRef, conversationId={convId})…");
            try
            {
#pragma warning disable OPENAI001
                var responses = new List<OpenAI.Responses.ResponseResult>();
                await foreach (var r in responsesClient.GetProjectResponsesAsync(agentRef, convId))
                    responses.Add(r);
#pragma warning restore OPENAI001

                log.AppendLine($"GetProjectResponsesAsync → {responses.Count} response(s)");
                foreach (var r in responses)
                {
                    var raw = System.Text.Json.JsonSerializer.Serialize(r);
                    log.AppendLine($"  Raw: {raw[..Math.Min(500, raw.Length)]}");
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"GetProjectResponsesAsync threw {ex.GetType().Name}: {ex.Message[..Math.Min(400, ex.Message.Length)]}");
            }
        }

        var logPath = Path.Combine(Path.GetTempPath(), "hist-direct-sdk.txt");
        await File.WriteAllTextAsync(logPath, log.ToString());
        _output.WriteLine($"Log: {logPath}");
        _output.WriteLine(log.ToString());

        Assert.Fail("Intentional — read output above to see what the API returns");
    }
}
