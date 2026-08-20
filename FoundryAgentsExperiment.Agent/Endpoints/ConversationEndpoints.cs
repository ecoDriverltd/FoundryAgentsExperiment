using FoundryAgentsExperiment.Agent.AgentExtensions;
using FoundryAgentsExperiment.Agent.AgentServices;
using FoundryAgentsExperiment.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace FoundryAgentsExperiment.Agent.Endpoints;

public static class ConversationEndpoints
{
    public static WebApplication MapConversationEndpoints(
        this WebApplication app,
        string agentName,
        CosmosChatHistoryProvider chatHistoryProvider)
    {
        app.MapGet("/conversations/{conversationId}",
            async ([FromRoute] string conversationId, HttpContext context, CancellationToken ct) =>
            {
                var serviceProvider = context.RequestServices;
                var sessionStore = serviceProvider.GetRequiredService<AgentSessionStore>();
                var conversationIndexStore = serviceProvider.GetRequiredService<CosmosConversationIndexStore>();
                var httpContextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
                var userId = httpContextAccessor.GetAgentUserId();
                var indexEntry = await conversationIndexStore.GetConversationAsync(conversationId, userId, ct);
                if (indexEntry is null)
                {
                    return Results.NotFound();
                }

                var agent = serviceProvider.GetRequiredKeyedService<AIAgent>(agentName);
                var session = await sessionStore.GetSessionAsync(agent, conversationId, ct);
                var invokingContext = new ChatHistoryProvider.InvokingContext(agent, session, requestMessages: []);
                var messages = await chatHistoryProvider.InvokingAsync(invokingContext, ct);

                return Results.Ok(new ConversationDetail(
                    conversationId,
                    indexEntry.Title,
                    indexEntry.LastRunId,
                    messages.ToList()));
            });

        app.MapPut("/conversations/{conversationId}/continuation",
            async (
                [FromRoute] string conversationId,
                [FromBody] UpdateConversationContinuationRequest update,
                CosmosConversationIndexStore conversationIndexStore,
                IHttpContextAccessor httpContextAccessor,
                CancellationToken ct = default) =>
            {
                if (string.IsNullOrWhiteSpace(update.RunId))
                {
                    return Results.BadRequest();
                }

                var userId = httpContextAccessor.GetAgentUserId();
                await conversationIndexStore.RecordConversationTurnAsync(
                    conversationId,
                    userId,
                    update.InitialUserPrompt,
                    ct);
                var updated = await conversationIndexStore.UpdateLastRunIdAsync(conversationId, userId, update.RunId, ct);
                return updated ? Results.NoContent() : Results.NotFound();
            });

        app.MapGet("/conversations",
            async (
                CosmosConversationIndexStore conversationIndexStore,
                IHttpContextAccessor httpContextAccessor,
                CancellationToken ct = default) =>
            {
                var userId = httpContextAccessor.GetAgentUserId();
                var conversations = await conversationIndexStore.ListConversationsAsync(userId, ct);
                return conversations.Select(conversation => new ConversationSummary(
                    conversation.Id,
                    conversation.Title,
                    conversation.CreatedAt,
                    conversation.LastUpdatedAt,
                    conversation.LastRunId));
            });

        return app;
    }
}
