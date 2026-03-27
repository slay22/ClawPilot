namespace ClawPilot.Worker.Models;

public class ToolOutcome
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ToolIntentId { get; set; }
    public Guid SessionId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string PermissionTier { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public bool Succeeded { get; set; }
    public long LatencyMs { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }

    public ToolIntent Intent { get; set; } = null!;
    public AgentSession Session { get; set; } = null!;
}
