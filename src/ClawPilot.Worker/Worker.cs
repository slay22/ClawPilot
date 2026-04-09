using ClawPilot.Worker.Models;
using ClawPilot.Worker.Services;

namespace ClawPilot.Worker;

public class Worker(
    TelegramService telegramService,
    CopilotService copilotService,
    ConversationService conversationService,
    SchedulerService schedulerService,
    LogService logService,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ClawPilot Worker starting...");

        _ = logService.StartAsync(stoppingToken);
        _ = telegramService.StartReceivingAsync(stoppingToken);

        try
        {
            await telegramService.SendMessageAsync(
                "🤖 *Claw\\-Pilot Agent Online*\n\n" +
                "• `/task <prompt>` — run a one\\-off agent task\n" +
                "• `/cron add [until\\-done|once] <schedule> [needs\\-approval] | <prompt>` — schedule a recurring task\n" +
                "• `/cron list` — list all scheduled tasks\n" +
                "• `/cron remove <id>` — remove a task\n" +
                "• `/cron pause <id>` / `/cron resume <id>` — pause or resume\n" +
                "• `/clear` — clear conversation history\n\n" +
                "_Schedule examples:_ `every 1h`, `every 30m`, `every day at 09:00`, `0 \\* \\* \\* \\*`",
                stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send startup message — verify Telegram:ChatId is your numeric user ID, not the bot username. " +
                                "Get it by messaging @userinfobot on Telegram");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TelegramCommand command = await telegramService.CommandReader.ReadAsync(stoppingToken);
                string chatId = command.ChatId;
                string text = command.Text.Trim();

                if (text.Equals("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    await conversationService.ClearHistoryAsync(chatId, stoppingToken);
                }
                else if (text.StartsWith("/task ", StringComparison.OrdinalIgnoreCase))
                {
                    string prompt = text["/task ".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(prompt))
                        await telegramService.SendMessageAsync("⚠️ Usage: `/task <what you want done>`", stoppingToken);
                    else
                    {
                        logger.LogInformation("Task mode: {Prompt}", prompt);
                        await copilotService.RunAgentAsync(prompt, $"Telegram:{chatId}", stoppingToken);
                    }
                }
                else if (text.StartsWith("/cron", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCronCommandAsync(text, stoppingToken);
                }
                else
                {
                    logger.LogInformation("Conversation mode for chat {ChatId}", chatId);
                    await conversationService.ChatAsync(chatId, text, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in agent loop");
                await telegramService.SendMessageAsync($"❌ *Unexpected Error:*\n\n{ex.Message}", stoppingToken);
            }
        }
    }

    // ─── /cron command handling ───────────────────────────────────────────────

    private async Task HandleCronCommandAsync(string text, CancellationToken ct)
    {
        string[] parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            await SendCronHelpAsync(ct);
            return;
        }

        string sub = parts[1].ToLowerInvariant();

        switch (sub)
        {
            case "list":
                await HandleCronListAsync(ct);
                break;

            case "add":
                string addArgs = parts.Length >= 3 ? parts[2] : string.Empty;
                await HandleCronAddAsync(addArgs, ct);
                break;

            case "remove":
            case "delete":
                string removeId = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
                await HandleCronRemoveAsync(removeId, ct);
                break;

            case "pause":
                string pauseId = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
                await HandleCronSetEnabledAsync(pauseId, enabled: false, ct);
                break;

            case "resume":
                string resumeId = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
                await HandleCronSetEnabledAsync(resumeId, enabled: true, ct);
                break;

            default:
                await SendCronHelpAsync(ct);
                break;
        }
    }

    private async Task HandleCronListAsync(CancellationToken ct)
    {
        List<ScheduledTask> tasks = await schedulerService.ListTasksAsync(ct);

        if (tasks.Count == 0)
        {
            await telegramService.SendMessageAsync("📭 No scheduled tasks\\.", ct);
            return;
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("📅 *Scheduled Tasks*\n");

        foreach (ScheduledTask t in tasks)
        {
            string status = t.IsEnabled ? "▶️" : "⏸";
            string mode = t.RunMode switch
            {
                RunMode.UntilDone => " \\[until\\-done\\]",
                RunMode.RunOnce   => " \\[once\\]",
                _                 => string.Empty,
            };
            string approval = t.NeedsApproval ? " 🔔" : string.Empty;
            string shortId = t.Id.ToString()[..8];
            string next = t.IsEnabled
                ? $"Next: {t.NextRunAt:yyyy\\-MM\\-dd HH:mm} UTC"
                : "Paused";

            sb.AppendLine($"{status} `{shortId}` {EscapeMarkdown(t.Label)}{mode}{approval}");
            sb.AppendLine($"   _{EscapeMarkdown(t.CronExpression)}_ · {next} · runs: {t.RunCount}");
            sb.AppendLine();
        }

        sb.AppendLine("_Use the first 8 chars of the ID with /cron remove or /cron pause_");
        await telegramService.SendMessageAsync(sb.ToString(), ct);
    }

    private async Task HandleCronAddAsync(string args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args) || !args.Contains('|'))
        {
            await telegramService.SendMessageAsync(
                "⚠️ Usage:\n`/cron add [until\\-done|once] <schedule> [needs\\-approval] | <prompt>`\n\n" +
                "Examples:\n" +
                "`/cron add every 1h | Check inbox for emails from boss`\n" +
                "`/cron add until\\-done every 30m needs\\-approval | Wait for invoice and forward it`\n" +
                "`/cron add once at 15:00 | Remind me to review the PR`", ct);
            return;
        }

        int pipeIdx = args.IndexOf('|');
        string schedulePart = args[..pipeIdx].Trim();
        string prompt = args[(pipeIdx + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            await telegramService.SendMessageAsync("⚠️ Prompt (after `|`) must not be empty\\.", ct);
            return;
        }

        RunMode runMode = RunMode.Recurring;
        if (schedulePart.StartsWith("until-done ", StringComparison.OrdinalIgnoreCase))
        {
            runMode = RunMode.UntilDone;
            schedulePart = schedulePart["until-done ".Length..].Trim();
        }
        else if (schedulePart.StartsWith("once ", StringComparison.OrdinalIgnoreCase))
        {
            runMode = RunMode.RunOnce;
            schedulePart = schedulePart["once ".Length..].Trim();
        }

        bool needsApproval = false;
        if (schedulePart.EndsWith(" needs-approval", StringComparison.OrdinalIgnoreCase))
        {
            needsApproval = true;
            schedulePart = schedulePart[..^" needs-approval".Length].Trim();
        }
        else if (schedulePart.EndsWith(" needs_approval", StringComparison.OrdinalIgnoreCase))
        {
            needsApproval = true;
            schedulePart = schedulePart[..^" needs_approval".Length].Trim();
        }

        if (string.IsNullOrWhiteSpace(schedulePart))
        {
            await telegramService.SendMessageAsync("⚠️ Schedule expression must not be empty\\.", ct);
            return;
        }

        try
        {
            ScheduledTask task = await schedulerService.AddTaskAsync(prompt, schedulePart, runMode, needsApproval, ct);

            string modeLabel = runMode switch
            {
                RunMode.UntilDone => " \\[until\\-done\\]",
                RunMode.RunOnce   => " \\[once\\]",
                _                 => string.Empty,
            };
            string approvalLabel = needsApproval ? " 🔔 needs\\-approval" : string.Empty;

            await telegramService.SendMessageAsync(
                $"✅ *Scheduled task added*{modeLabel}{approvalLabel}\n\n" +
                $"ID: `{task.Id.ToString()[..8]}`\n" +
                $"Schedule: `{EscapeMarkdown(schedulePart)}`\n" +
                $"Next run: {task.NextRunAt:yyyy\\-MM\\-dd HH:mm} UTC\n" +
                $"Prompt: _{EscapeMarkdown(task.Label)}_", ct);
        }
        catch (ArgumentException ex)
        {
            await telegramService.SendMessageAsync(
                $"⚠️ Invalid schedule: {EscapeMarkdown(ex.Message)}", ct);
        }
    }

    private async Task HandleCronRemoveAsync(string idPrefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idPrefix))
        {
            await telegramService.SendMessageAsync("⚠️ Usage: `/cron remove <id>`", ct);
            return;
        }

        Guid? resolvedId = await ResolveTaskIdAsync(idPrefix, ct);
        if (resolvedId is null)
        {
            await telegramService.SendMessageAsync(
                $"⚠️ No task found with ID starting with `{EscapeMarkdown(idPrefix)}`\\.", ct);
            return;
        }

        bool removed = await schedulerService.RemoveTaskAsync(resolvedId.Value, ct);
        await telegramService.SendMessageAsync(
            removed
                ? $"🗑 Task `{EscapeMarkdown(idPrefix)}` removed\\."
                : $"⚠️ Task `{EscapeMarkdown(idPrefix)}` not found\\.", ct);
    }

    private async Task HandleCronSetEnabledAsync(string idPrefix, bool enabled, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idPrefix))
        {
            string verb = enabled ? "resume" : "pause";
            await telegramService.SendMessageAsync($"⚠️ Usage: `/cron {verb} <id>`", ct);
            return;
        }

        Guid? resolvedId = await ResolveTaskIdAsync(idPrefix, ct);
        if (resolvedId is null)
        {
            await telegramService.SendMessageAsync(
                $"⚠️ No task found with ID starting with `{EscapeMarkdown(idPrefix)}`\\.", ct);
            return;
        }

        bool ok = await schedulerService.SetEnabledAsync(resolvedId.Value, enabled, ct);
        string label = enabled ? "resumed ▶️" : "paused ⏸";
        await telegramService.SendMessageAsync(
            ok
                ? $"Task `{EscapeMarkdown(idPrefix)}` {label}\\."
                : $"⚠️ Task `{EscapeMarkdown(idPrefix)}` not found\\.", ct);
    }

    private async Task<Guid?> ResolveTaskIdAsync(string idPrefix, CancellationToken ct)
    {
        List<ScheduledTask> all = await schedulerService.ListTasksAsync(ct);
        ScheduledTask? match = all.FirstOrDefault(t =>
            t.Id.ToString().StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase));
        return match?.Id;
    }

    private Task SendCronHelpAsync(CancellationToken ct) =>
        telegramService.SendMessageAsync(
            "*Cron commands:*\n\n" +
            "`/cron add [until\\-done|once] <schedule> [needs\\-approval] | <prompt>`\n" +
            "`/cron list`\n" +
            "`/cron remove <id>`\n" +
            "`/cron pause <id>`\n" +
            "`/cron resume <id>`\n\n" +
            "*Schedule examples:*\n" +
            "`every 5m` · `every 1h` · `every day` · `every day at 09:00`\n" +
            "`0 \\* \\* \\* \\*` \\(standard cron\\)", ct);

    private static string EscapeMarkdown(string text) =>
        text.Replace("\\", "\\\\").Replace("_", "\\_").Replace("*", "\\*")
            .Replace("`", "\\`").Replace("[", "\\[").Replace(".", "\\.").Replace("!", "\\!");
}
