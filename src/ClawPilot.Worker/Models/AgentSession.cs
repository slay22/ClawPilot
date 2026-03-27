namespace ClawPilot.Worker.Models;

public class AgentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public string TriggerSource { get; set; } = string.Empty;
    public string InitialPrompt { get; set; } = string.Empty;
    public string Status { get; set; } = AgentSessionStatus.Running;
    public int IterationsUsed { get; set; }
    public int ToolCallsUsed { get; set; }
    public string? FinalSummary { get; set; }
    public string? FaultReason { get; set; }

    public List<ThoughtStep> ThoughtSteps { get; set; } = [];
    public List<ToolIntent> ToolIntents { get; set; } = [];
    public List<CorrectionStep> CorrectionSteps { get; set; } = [];
    public SessionSummary? Summary { get; set; }
}

public static class AgentSessionStatus
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string BudgetExhausted = "BudgetExhausted";
    public const string Denied = "Denied";
    public const string Faulted = "Faulted";
}
