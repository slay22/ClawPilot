namespace ClawPilot.Worker.Models;

public class ToolIntent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int SequenceNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ToolName { get; set; } = string.Empty;
    public string ToolSource { get; set; } = ToolSources.Local;
    public string ArgumentsJson { get; set; } = string.Empty;
    public string? ReasoningExcerpt { get; set; }

    public AgentSession Session { get; set; } = null!;
    public ToolOutcome? Outcome { get; set; }
}

public static class ToolSources
{
    public const string Local = "Local";
    public const string GitHubMcp = "GitHubMCP";
    public const string WebSearchMcp = "WebSearchMCP";
}
