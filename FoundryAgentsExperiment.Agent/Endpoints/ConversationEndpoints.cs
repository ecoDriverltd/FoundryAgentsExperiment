using FoundryAgentsExperiment.Agent.AgentServices;
using FoundryAgentsExperiment.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc;

namespace FoundryAgentsExperiment.Agent.Endpoints;

public static class ConversationEndpoints
{
    public static WebApplication MapConversationEndpoints(
        this WebApplication app,
        string agentName)
    {
        app.MapGet("/conversations/{conversationId}",
            async ([FromRoute] string conversationId, HttpContext context, CancellationToken ct) =>
            {
                var serviceProvider = context.RequestServices;
                var sessionStore = serviceProvider.GetRequiredKeyedService<CosmosAgentSessionStore>(agentName);
                var entry = await sessionStore.GetConversationAsync(conversationId, ct);
                if (entry is null)
                {
                    return Results.NotFound();
                }

                var agent = serviceProvider.GetRequiredKeyedService<AIAgent>(agentName);
                var messages = await sessionStore.GetConversationMessagesAsync(agent, conversationId, ct);

                return Results.Ok(new ConversationDetail(
                    conversationId,
                    entry.Title,
                    messages ?? []));
            });

        app.MapGet("/conversations",
            async (
                HttpContext context,
                CancellationToken ct = default) =>
            {
                var sessionStore = context.RequestServices.GetRequiredKeyedService<CosmosAgentSessionStore>(agentName);
                var conversations = await sessionStore.ListConversationsAsync(ct);
                return conversations.Select(conversation => new ConversationSummary(
                    conversation.Id,
                    conversation.Title,
                    conversation.CreatedAt,
                    conversation.LastUpdatedAt));
            });

        app.MapDelete("/conversations/{conversationId}",
            async ([FromRoute] string conversationId, HttpContext context, CancellationToken ct) =>
            {
                var sessionStore = context.RequestServices.GetRequiredKeyedService<CosmosAgentSessionStore>(agentName);
                return await sessionStore.SoftDeleteConversationAsync(conversationId, ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            });

        return app;
    }
}
