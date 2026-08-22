using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FoundryAgentsExperiment.SampleParityTests;

[Trait("Category", "Integration")]
public sealed class SampleParitySessionStoreTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private readonly ITestOutputHelper output = output;
    private DistributedApplication? app;

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", null);
        Environment.SetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES", null);

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FoundryAgentsExperiment_AppHost>(
            args: [],
            configureBuilder: (appOptions, _) => appOptions.DisableDashboard = true,
            cancellationToken: TestContext.Current.CancellationToken);
        builder.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Information));
        builder.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
                options.CircuitBreaker.SamplingDuration = options.AttemptTimeout.Timeout * 2;
            });
        });

        app = await builder.BuildAsync(TestContext.Current.CancellationToken).WaitAsync(StartupTimeout, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken).WaitAsync(StartupTimeout, TestContext.Current.CancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("agent-test-sw", TestContext.Current.CancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("sample-parity-agent", TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task DefaultPersistence_NormalContinuation_ReplaysTheFirstPrompt()
    {
        const string firstPrompt = "Reply with exactly: baseline continuation one.";
        const string secondPrompt = "Reply with exactly: baseline continuation two.";
        using var http = CreateHttpClient();
        var client = await SampleParityTestClient.CreateAsync(http, "/", TestContext.Current.CancellationToken);

        await client.SendAsync(firstPrompt, TestContext.Current.CancellationToken);
        await client.SendAsync(secondPrompt, TestContext.Current.CancellationToken);

        var history = await GetHistoryAsync(http, "sample-parity-agent", client.ThreadId!, TestContext.Current.CancellationToken);
        Assert.Equal(2, CountUserPrompt(history, firstPrompt));
        Assert.Equal(1, CountUserPrompt(history, secondPrompt));
    }

    [Fact]
    public async Task DefaultPersistence_SecondTurnServerTool_PersistsToolPromptOnce()
    {
        const string firstPrompt = "Reply with exactly: baseline before time.";
        const string secondPrompt = "Use the get_current_time tool and report the current UTC time.";
        using var http = CreateHttpClient();
        var client = await SampleParityTestClient.CreateAsync(http, "/", TestContext.Current.CancellationToken);

        await client.SendAsync(firstPrompt, TestContext.Current.CancellationToken);
        var toolRun = await client.SendAsync(secondPrompt, TestContext.Current.CancellationToken);

        Assert.Contains(toolRun.FunctionCalls, call => call.Name == "get_current_time");
        Assert.Contains(toolRun.FunctionResults, result => !string.IsNullOrWhiteSpace(result.CallId));
        var history = await GetHistoryAsync(http, "sample-parity-agent", client.ThreadId!, TestContext.Current.CancellationToken);
        Assert.Equal(1, CountUserPrompt(history, secondPrompt));
        Assert.Contains(history, message => ContainsFunctionContent(message, "functionCall"));
        Assert.Contains(history, message => ContainsFunctionContent(message, "functionResult"));
    }

    [Fact]
    public async Task DefaultPersistence_FreshClientSessionSecondTurnServerTool_PersistsToolPromptOnce()
    {
        const string firstPrompt = "Reply with exactly: baseline before time with a fresh session.";
        const string secondPrompt = "Use the get_current_time tool and report the current UTC time.";
        using var http = CreateHttpClient();
        var client = await SampleParityTestClient.CreateAsync(
            http,
            "/",
            TestContext.Current.CancellationToken,
            createSessionPerTurn: true);

        await client.SendAsync(firstPrompt, TestContext.Current.CancellationToken);
        var toolRun = await client.SendAsync(secondPrompt, TestContext.Current.CancellationToken);

        Assert.Contains(toolRun.FunctionCalls, call => call.Name == "get_current_time");
        var history = await GetHistoryAsync(http, "sample-parity-agent", client.ThreadId!, TestContext.Current.CancellationToken);
        Assert.Equal(1, CountUserPrompt(history, secondPrompt));
    }

    [Fact]
    public async Task DefaultPersistence_ClientToolContinuation_ReplaysPrompt()
    {
        const string prompt = "Use the change_background_color tool, then confirm that it was changed.";
        using var http = CreateHttpClient();
        var client = await SampleParityTestClient.CreateAsync(http, "/", TestContext.Current.CancellationToken);

        var run = await client.SendAsync(prompt, TestContext.Current.CancellationToken);

        Assert.Contains(run.FunctionCalls, call => call.Name == "change_background_color");
        Assert.Contains(run.FunctionResults, result => !string.IsNullOrWhiteSpace(result.CallId));
        Assert.True(run.RunIds.Count >= 2, "A client tool result must continue the AG-UI run.");
        var history = await GetHistoryAsync(http, "sample-parity-agent", client.ThreadId!, TestContext.Current.CancellationToken);
        Assert.Equal(2, CountUserPrompt(history, prompt));
    }

    [Fact]
    public async Task PerServicePersistence_ClientToolContinuation_ReplaysPrompt()
    {
        const string prompt = "Use the change_background_color tool, then confirm that it was changed.";
        using var http = CreateHttpClient();
        var client = await SampleParityTestClient.CreateAsync(http, "/per-service", TestContext.Current.CancellationToken);

        var run = await client.SendAsync(prompt, TestContext.Current.CancellationToken);

        Assert.Contains(run.FunctionCalls, call => call.Name == "change_background_color");
        var history = await GetHistoryAsync(http, "sample-parity-per-service-agent", client.ThreadId!, TestContext.Current.CancellationToken);
        Assert.Equal(2, CountUserPrompt(history, prompt));
    }

    private HttpClient CreateHttpClient() => app!.CreateHttpClient("sample-parity-agent");

    private async Task<JsonElement[]> GetHistoryAsync(HttpClient http, string agentId, string threadId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            $"/_diagnostics/sessions/{Uri.EscapeDataString(agentId)}/{Uri.EscapeDataString(threadId)}",
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"No diagnostic StateBag returned for thread '{threadId}'. Status={(int)response.StatusCode}. {responseBody}");
        }
        using var document = JsonDocument.Parse(responseBody);
        var session = document.RootElement.Clone();
        var history = session
            .GetProperty("stateBag")
            .GetProperty("InMemoryChatHistoryProvider")
            .GetProperty("messages")
            .EnumerateArray()
            .ToArray();
        output.WriteLine($"StateBag for {agentId}/{threadId}:{Environment.NewLine}{string.Join(Environment.NewLine, history.Select(message => message.GetRawText()))}");
        return history;
    }

    private static int CountUserPrompt(IEnumerable<JsonElement> history, string prompt) =>
        history.Count(message =>
            message.TryGetProperty("role", out var role) && role.GetString() == "user" &&
            message.TryGetProperty("contents", out var contents) &&
            contents.EnumerateArray().Any(content =>
                content.TryGetProperty("text", out var text) && text.GetString() == prompt));

    private static bool ContainsFunctionContent(JsonElement message, string contentType) =>
        message.TryGetProperty("contents", out var contents) &&
        contents.EnumerateArray().Any(content =>
            content.TryGetProperty("$type", out var type) && type.GetString()?.Contains(contentType, StringComparison.OrdinalIgnoreCase) == true);
}
