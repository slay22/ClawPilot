namespace ClawPilot.Worker.Models;

public enum RunMode
{
    Recurring,  // runs forever on schedule
    UntilDone,  // runs until agent calls complete_task()
    RunOnce,    // fires once then auto-removed
}

public class ScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Short human-readable label, auto-derived from the first 60 chars of Prompt.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The agent prompt to run each occurrence.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Cron expression (e.g. "0 * * * *") used for scheduling. Stored in UTC.</summary>
    public string CronExpression { get; set; } = string.Empty;

    public RunMode RunMode { get; set; } = RunMode.Recurring;

    /// <summary>When true, operator must approve via Telegram before each occurrence runs.</summary>
    public bool NeedsApproval { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int RunCount { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public DateTimeOffset NextRunAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
