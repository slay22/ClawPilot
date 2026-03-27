namespace ClawPilot.Worker.Options;

public enum JournalVerbosity { Full, Standard, Minimal }

public class JournalOptions
{
    public JournalVerbosity Verbosity { get; set; } = JournalVerbosity.Full;
    public int RetainThoughtStepsDays { get; set; } = 7;
}
