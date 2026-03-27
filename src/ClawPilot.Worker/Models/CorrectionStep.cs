namespace ClawPilot.Worker.Models;

public class CorrectionStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int IterationNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string FailedApproach { get; set; } = string.Empty;
    public string ErrorSignal { get; set; } = string.Empty;
    public string RevisedApproach { get; set; } = string.Empty;

    public AgentSession Session { get; set; } = null!;
}
