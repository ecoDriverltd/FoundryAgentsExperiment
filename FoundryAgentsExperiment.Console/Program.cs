// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;

return;

string serverUrl = Environment.GetEnvironmentVariable("services__agent-dotnet__https__0") ?? "http://localhost:8888";

Console.WriteLine($"Connecting to AG-UI server at: {serverUrl}\n");

// Create the AG-UI client agent
using HttpClient httpClient = new()
{
    Timeout = TimeSpan.FromSeconds(60)
};

AGUIChatClient chatClient = new(httpClient, serverUrl + "/ag-ui");

ChatClientAgent agent = chatClient.AsAIAgent(
    name: "agui-client",
    description: "AG-UI Client Agent");

AgentSession session = await agent.CreateSessionAsync();
List<ChatMessage> messages =
[
    new(ChatRole.System, "You are a helpful assistant.")
];

bool isFirstUpdate = true;
string? threadId = null;
string? threadId2 = null;

try
{
    messages.Add(new ChatMessage(ChatRole.User, "My favourite colour is BLUE42."));

    string response1 = string.Empty;
    string errorMessage = string.Empty;

    // Stream the response.
    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
    {
        ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

        // First update indicates run started
        if (isFirstUpdate)
        {
            threadId = chatUpdate.ConversationId;
            isFirstUpdate = false;
        }

        // Display streaming text content
        foreach (AIContent content in update.Contents)
        {
            if (content is TextContent textContent)
            {
                response1 += textContent.Text;
            }
            else if (content is ErrorContent errorContent)
            {
                errorMessage = errorContent.Message;
            }
        }
    }

    if (string.IsNullOrEmpty(threadId))
    {
        Console.WriteLine("Error: Thread ID is null or empty after first response.");
        return;
    }

    messages = [new ChatMessage(ChatRole.User, "What is my favourite colour?")];
    AgentSession session2 = await agent.CreateSessionAsync(threadId);
    string response2 = string.Empty;
    string errorMessage2 = string.Empty;
    isFirstUpdate = false;

    // Stream the response.
    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session2))
    {
        ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

        // First update indicates run started
        if (isFirstUpdate)
        {
            threadId2 = chatUpdate.ConversationId;
            isFirstUpdate = false;
        }

        // Display streaming text content
        foreach (AIContent content in update.Contents)
        {
            if (content is TextContent textContent)
            {
                response2 += textContent.Text;
            }
            else if (content is ErrorContent errorContent)
            {
                errorMessage2 = errorContent.Message;
            }
        }
    }

    Console.WriteLine($"\nFirst response: {response1}");
    Console.WriteLine($"\nSecond response: {response2}");


    //while (true)
    //{
    //    // Get user input
    //    Console.Write("\nUser (:q or quit to exit): ");
    //    string? message = Console.ReadLine();

    //    if (string.IsNullOrWhiteSpace(message))
    //    {
    //        Console.WriteLine("Request cannot be empty.");
    //        continue;
    //    }

    //    if (message is ":q" or "quit")
    //    {
    //        break;
    //    }

    //    messages.Add(new ChatMessage(ChatRole.User, message));

    //    // Stream the response
    //    bool isFirstUpdate = true;
    //    string? threadId = null;

    //    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
    //    {
    //        ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

    //        // First update indicates run started
    //        if (isFirstUpdate)
    //        {
    //            threadId = chatUpdate.ConversationId;
    //            Console.ForegroundColor = ConsoleColor.Yellow;
    //            Console.WriteLine($"\n[Run Started - Thread: {chatUpdate.ConversationId}, Run: {chatUpdate.ResponseId}]");
    //            Console.ResetColor();
    //            isFirstUpdate = false;
    //        }

    //        // Display streaming text content
    //        foreach (AIContent content in update.Contents)
    //        {
    //            if (content is TextContent textContent)
    //            {
    //                Console.ForegroundColor = ConsoleColor.Cyan;
    //                Console.Write(textContent.Text);
    //                Console.ResetColor();
    //            }
    //            else if (content is ErrorContent errorContent)
    //            {
    //                Console.ForegroundColor = ConsoleColor.Red;
    //                Console.WriteLine($"\n[Error: {errorContent.Message}]");
    //                Console.ResetColor();
    //            }
    //        }
    //    }

    //    Console.ForegroundColor = ConsoleColor.Green;
    //    Console.WriteLine($"\n[Run Finished - Thread: {threadId}]");
    //    Console.ResetColor();
    //}
}
catch (Exception ex)
{
    Console.WriteLine($"\nAn error occurred: {ex.Message}");
}