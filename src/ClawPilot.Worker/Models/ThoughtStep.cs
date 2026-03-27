namespace ClawPilot.Worker.Models;

public class ThoughtStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int SequenceNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ReasoningText { get; set; } = string.Empty;

    public AgentSession Session { get; set; } = null!;
}
