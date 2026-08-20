namespace FoundryAgentsExperiment.Agent.AgentExtensions;

public static class AgentUserId
{
    extension(IHttpContextAccessor httpContextAccessor)
    {
        public string GetAgentUserId()
        {
            string? userId = httpContextAccessor.HttpContext?.Request.Headers["x-agent-user-id"].ToString();

            if (string.IsNullOrEmpty(userId))
            {
                return "annonymous"; // Fallback for local dev if Foundry doesn't inject the header
            }

            return userId;
        }
    }
}
