namespace ClawPilot.Worker.Options;

public class AgentBudgetOptions
{
    public int MaxIterations { get; set; } = 15;
    public int MaxToolCallsPerSession { get; set; } = 40;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
}
