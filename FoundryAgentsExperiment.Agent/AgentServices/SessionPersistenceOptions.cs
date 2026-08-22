namespace FoundryAgentsExperiment.Agent.AgentServices;

public sealed class SessionPersistenceOptions
{
    public const string SectionName = "SessionPersistence";

    public const int CosmosItemLimitBytes = 2 * 1024 * 1024;

    public int CompactionTriggerTokens { get; set; } = 100_000;

    public int CompactionMinimumPreservedGroups { get; set; } = 20;

    public int WarningThresholdBytes { get; set; } = 1_572_864;

    public int MaximumSerializedSessionBytes { get; set; } = 1_835_008;
}