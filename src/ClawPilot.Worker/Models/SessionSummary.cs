namespace ClawPilot.Worker.Models;

public class SessionSummary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string FinalAnswer { get; set; } = string.Empty;
    public int CorrectionsCount { get; set; }
    public int ThoughtStepsCount { get; set; }
    public string BudgetConsumedJson { get; set; } = string.Empty;

    public AgentSession Session { get; set; } = null!;
}
