using Cronos;
using ClawPilot.Worker.Data;
using ClawPilot.Worker.Models;
using Microsoft.EntityFrameworkCore;

namespace ClawPilot.Worker.Services;

/// <summary>
/// Manages scheduled (cron-style) tasks. Ticks every 30 seconds, fires due tasks,
/// handles the NeedsApproval gate, RunOnce auto-removal, and UntilDone completion.
/// </summary>
public class SchedulerService(
    IDbContextFactory<ClawPilotDbContext> dbFactory,
    TelegramService telegramService,
    ILogger<SchedulerService> logger) : BackgroundService
{
    // Due tasks are written here; the Worker main loop (or an internal drainer) reads them.
    private readonly System.Threading.Channels.Channel<ScheduledTask> _dueChannel =
        System.Threading.Channels.Channel.CreateUnbounded<ScheduledTask>();

    // Injected by Program.cs after construction (breaks circular dependency).
    public CopilotService? CopilotService { get; set; }

    public System.Threading.Channels.ChannelReader<ScheduledTask> DueTaskReader => _dueChannel.Reader;

    // ─── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SchedulerService started");

        // Drain and run due tasks serially in the background.
        Task drainer = Task.Run(() => DrainDueTasksAsync(stoppingToken), stoppingToken);

        PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EnqueueDueTasksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in scheduler tick");
            }
        }

        await drainer;
    }

    // ─── Tick — enqueue due tasks ─────────────────────────────────────────────

    private async Task EnqueueDueTasksAsync(CancellationToken ct)
    {
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        List<ScheduledTask> due = await db.ScheduledTasks
            .Where(t => t.IsEnabled && t.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (ScheduledTask task in due)
        {
            logger.LogInformation("Scheduling due task {Id} '{Label}' (RunMode={Mode})", task.Id, task.Label, task.RunMode);
            await _dueChannel.Writer.WriteAsync(task, ct);

            // Advance NextRunAt immediately so the same tick doesn't re-enqueue.
            try
            {
                CronExpression cron = CronScheduleParser.Parse(task.CronExpression);
                task.NextRunAt = CronScheduleParser.NextOccurrence(cron, now);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not advance NextRunAt for task {Id}", task.Id);
            }
        }
    }

    // ─── Drainer — run tasks serially ─────────────────────────────────────────

    private async Task DrainDueTasksAsync(CancellationToken ct)
    {
        await foreach (ScheduledTask task in _dueChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await RunTaskAsync(task, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error running scheduled task {Id}", task.Id);
                await telegramService.SendMessageAsync(
                    $"❌ *Scheduled task failed:* _{EscapeMarkdown(task.Label)}_\n\n{EscapeMarkdown(ex.Message)}", ct);
            }
        }
    }

    private async Task RunTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        if (task.NeedsApproval)
        {
            bool approved = await telegramService.RequestPermissionAsync(
                $"🕐 Scheduled task is due:\n\n_{EscapeMarkdown(task.Label)}_\n\nApprove this run?", ct);

            if (!approved)
            {
                logger.LogInformation("Scheduled task {Id} skipped by operator", task.Id);
                await telegramService.SendMessageAsync(
                    $"⏭ Scheduled task _{EscapeMarkdown(task.Label)}_ skipped\\.", ct);
                return;
            }
        }

        await telegramService.SendMessageAsync(
            $"🕐 *Running scheduled task:* _{EscapeMarkdown(task.Label)}_", ct);

        // For UntilDone tasks, inject a hint so the agent knows to call complete_task.
        string prompt = task.RunMode == RunMode.UntilDone
            ? $"{task.Prompt}\n\n[System: When you have fully achieved the goal above, call the complete_task tool with task_id=\"{task.Id}\".]"
            : task.Prompt;

        if (CopilotService is not null)
            await CopilotService.RunAgentAsync(prompt, $"Scheduler:{task.Id}", ct);

        // Post-run bookkeeping
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ScheduledTask? stored = await db.ScheduledTasks.FindAsync([task.Id], ct);
        if (stored is null) return; // was completed/removed mid-run

        stored.LastRunAt = DateTimeOffset.UtcNow;
        stored.RunCount++;

        if (stored.RunMode == RunMode.RunOnce)
        {
            db.ScheduledTasks.Remove(stored);
            await db.SaveChangesAsync(ct);
            await telegramService.SendMessageAsync(
                $"🗑 One-time task _{EscapeMarkdown(task.Label)}_ completed and removed\\.", ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }
    }

    // ─── Public management API ────────────────────────────────────────────────

    public async Task<ScheduledTask> AddTaskAsync(
        string prompt, string schedule, RunMode runMode, bool needsApproval, CancellationToken ct)
    {
        CronExpression cron = CronScheduleParser.Parse(schedule);
        DateTimeOffset next = CronScheduleParser.NextOccurrence(cron, DateTimeOffset.UtcNow);

        ScheduledTask task = new()
        {
            Label = prompt.Length > 60 ? prompt[..60] + "…" : prompt,
            Prompt = prompt,
            CronExpression = schedule,
            RunMode = runMode,
            NeedsApproval = needsApproval,
            NextRunAt = next,
        };

        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync(ct);
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Added scheduled task {Id} '{Label}' next={Next}", task.Id, task.Label, next);
        return task;
    }

    public async Task<bool> RemoveTaskAsync(Guid id, CancellationToken ct)
    {
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ScheduledTask? task = await db.ScheduledTasks.FindAsync([id], ct);
        if (task is null) return false;
        db.ScheduledTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync(ct);
        ScheduledTask? task = await db.ScheduledTasks.FindAsync([id], ct);
        if (task is null) return false;
        task.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<ScheduledTask>> ListTasksAsync(CancellationToken ct)
    {
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScheduledTasks.OrderBy(t => t.NextRunAt).ToListAsync(ct);
    }

    [CopilotTool("complete_task",
        "Marks a scheduled task as fully achieved and removes it. " +
        "Call this when you have successfully completed the goal of an 'until-done' scheduled task. " +
        "Provide the task_id that was given to you in the system hint at the start of this run.")]
    public async Task<string> CompleteTaskAsync(string task_id)
    {
        if (!Guid.TryParse(task_id, out Guid id))
            return $"Invalid task_id '{task_id}'. Expected a GUID.";

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        bool removed = await RemoveTaskAsync(id, cts.Token);

        if (!removed)
            return $"Task '{task_id}' not found — it may have already been removed.";

        logger.LogInformation("Scheduled task {Id} marked complete by agent", id);
        await telegramService.SendMessageAsync(
            $"✅ Scheduled task goal achieved — task removed\\.", cts.Token);
        return $"Task '{task_id}' completed and removed from the scheduler.";
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string EscapeMarkdown(string text) =>
        text.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[");
}
