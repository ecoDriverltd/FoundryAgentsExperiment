using FoundryAgentsExperiment.Agent.AgentExtensions;
using Microsoft.Extensions.AI;
using Xunit;

namespace FoundryAgentsExperiment.IntegrationTests;

public class MemoryProviderFilterTests
{
    [Fact]
    public void FilterConversationMessages_KeepsPlainTextUserAndAssistantConversation()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "What are the best solar panels?"),
            new(ChatRole.Assistant, "I recommend comparing efficiency, warranty, and local installer support."),
        ];

        var eligibleMessages = MemoryProvider.FilterConversationMessages(messages).ToList();

        Assert.Equal(messages, eligibleMessages);
    }

    [Fact]
    public void FilterConversationMessages_ExcludesSystemAndToolProtocolContent()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, "Use the available tools."),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]),
            new(ChatRole.User, [new FunctionCallContent("call-2", "tool_name", new Dictionary<string, object?>())]),
        ];

        var eligibleMessages = MemoryProvider.FilterConversationMessages(messages);

        Assert.Empty(eligibleMessages);
    }

    [Fact]
    public void FilterSearchInputMessages_UsesOnlyPlainTextUserMessages()
    {
        var userMessage = new ChatMessage(ChatRole.User, "What solar panels did we discuss?");
        ChatMessage[] messages =
        [
            userMessage,
            new(ChatRole.Assistant, "You asked about solar panels."),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]),
        ];

        var eligibleMessages = MemoryProvider.FilterSearchInputMessages(messages).ToList();

        Assert.Equal([userMessage], eligibleMessages);
    }
}
