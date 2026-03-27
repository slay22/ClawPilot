using System.Collections.Concurrent;
using ClawPilot.Worker.Data;
using ClawPilot.Worker.Models;
using ClawPilot.Worker.Options;
using GitHub.Copilot.SDK;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Services;

public class ConversationService(
    TelegramService telegramService,
    IDbContextFactory<ClawPilotDbContext> dbFactory,
    IOptions<ConversationOptions> conversationOptions,
    IOptions<GitHubOptions> githubOptions,
    WebSearchMcpService webSearchMcp,
    ILogger<ConversationService> logger) : IHostedService, IAsyncDisposable
{
    private readonly ConversationOptions _opts = conversationOptions.Value;
    private readonly CopilotClient _client = new(new CopilotClientOptions { GithubToken = githubOptions.Value.CopilotToken });
    private readonly ConcurrentDictionary<string, CopilotSession> _sessions = new();

    // Set by Program.cs after CopilotService is created to break the circular dependency.
    public CopilotService? CopilotService { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken) => await _client.StartAsync();

    public async Task StopAsync(CancellationToken cancellationToken) => await _client.ForceStopAsync();

    public async Task ChatAsync(string chatId, string message, CancellationToken ct)
    {
        try
        {
            CopilotSession session = await GetOrCreateSessionAsync(chatId, ct);

            string fullReply = string.Empty;
            TaskCompletionSource done = new();
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(60));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using CancellationTokenRegistration reg = linkedCts.Token.Register(() => done.TrySetCanceled());

            using IDisposable sub = session.On(ev =>
            {
                logger.LogDebug("Session event [{ChatId}]: {Type}", chatId, ev.Type);
                switch (ev)
                {
                    case AssistantMessageDeltaEvent { Data.DeltaContent: string delta }:
                        fullReply += delta;
                        break;

                    case AssistantMessageEvent { Data.Content: string content } when string.IsNullOrEmpty(fullReply):
                        fullReply = content;
                        break;

                    case SessionIdleEvent:
                        done.TrySetResult();
                        break;

                    case SessionErrorEvent { Data.Message: string err }:
                        done.TrySetException(new Exception(err));
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = message });
            await done.Task;

            if (!string.IsNullOrWhiteSpace(fullReply))
            {
                await telegramService.SendMessageAsync(fullReply, ct);
                await UpdateSessionTimestampAsync(chatId, ct);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Conversation cancelled for chat {ChatId}", chatId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conversation error for chat {ChatId}", chatId);
            await telegramService.SendMessageAsync("❌ Sorry, something went wrong\\. Try again\\.", ct);
        }
    }

    public async Task ClearHistoryAsync(string chatId, CancellationToken ct)
    {
        if (_sessions.TryRemove(chatId, out CopilotSession? old))
        {
            string oldId = old.SessionId;
            await old.DisposeAsync();
            try { await _client.DeleteSessionAsync(oldId); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not delete SDK session {Id}", oldId); }
        }

        await DeleteStoredSessionAsync(chatId, ct);
        await telegramService.SendMessageAsync("🗑️ Conversation cleared\\.", ct);
        logger.LogInformation("Cleared conversation for chat {ChatId}", chatId);
    }

    private async Task<CopilotSession> GetOrCreateSessionAsync(string chatId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(chatId, out CopilotSession? existing))
            return existing;

        string? storedId = await GetStoredSessionIdAsync(chatId, ct);
        CopilotSession session;

        if (storedId is not null)
        {
            try
            {
                session = await _client.ResumeSessionAsync(storedId, new ResumeSessionConfig
                {
                    OnPermissionRequest = (_, _) => Task.FromResult(new PermissionRequestResult { Kind = "allow" })
                });
                logger.LogInformation("Resumed conversation session {SessionId} for chat {ChatId}", storedId, chatId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resume session {Id} — creating new one", storedId);
                session = await CreateNewSessionAsync(chatId, ct);
            }
        }
        else
        {
            session = await CreateNewSessionAsync(chatId, ct);
        }

        _sessions[chatId] = session;
        return session;
    }

    private async Task<CopilotSession> CreateNewSessionAsync(string chatId, CancellationToken ct)
    {
        List<AIFunction> tools = [..webSearchMcp.Tools, BuildEscalateTool(chatId, ct)];

        string soul = await LoadSoulAsync();
        string systemContent = string.IsNullOrWhiteSpace(soul)
            ? _opts.SystemPrompt
            : $"{soul}\n\n---\n\n{_opts.SystemPrompt}";

        CopilotSession session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = "gpt-4o",
            Streaming = true,
            Tools = tools,
            OnPermissionRequest = (_, _) => Task.FromResult(new PermissionRequestResult { Kind = "allow" }),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemContent
            }
        });

        await StoreSessionIdAsync(chatId, session.SessionId, ct);
        logger.LogInformation("Created new conversation session {SessionId} for chat {ChatId}", session.SessionId, chatId);
        return session;
    }

    private AIFunction BuildEscalateTool(string chatId, CancellationToken ct)
    {
        return AIFunctionFactory.Create(
            async (string taskDescription) =>
            {
                logger.LogInformation("Escalating to task mode for chat {ChatId}: {Task}", chatId, taskDescription);
                await telegramService.SendMessageAsync("🔀 *Escalating to task mode\\.\\.\\.*", ct);

                if (CopilotService is not null)
                    await CopilotService.RunAgentAsync(taskDescription, $"Conversation:{chatId}", ct);
                else
                    await telegramService.SendMessageAsync("❌ Task mode unavailable\\.", ct);

                return "Task escalated.";
            },
            "escalate_to_task",
            "Escalate the user's request to full task mode when it requires running builds, fixing code, " +
            "querying databases, modifying files, or interacting with GitHub repositories. " +
            "Provide a clear, self-contained description of what needs to be done.");
    }

    private async Task<string?> GetStoredSessionIdAsync(string chatId, CancellationToken ct)
    {
        ClawPilotDbContext db = dbFactory.CreateDbContext();
        await using (db)
        {
            ConversationMessage? row = await db.ConversationMessages
                .FirstOrDefaultAsync(m => m.TelegramChatId == chatId, ct);
            return row?.CopilotSessionId;
        }
    }

    private async Task StoreSessionIdAsync(string chatId, string sessionId, CancellationToken ct)
    {
        ClawPilotDbContext db = dbFactory.CreateDbContext();
        await using (db)
        {
            ConversationMessage? existing = await db.ConversationMessages.FirstOrDefaultAsync(m => m.TelegramChatId == chatId, ct);

            if (existing is not null)
            {
                existing.CopilotSessionId = sessionId;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.ConversationMessages.Add(new ConversationMessage
                {
                    TelegramChatId = chatId,
                    CopilotSessionId = sessionId
                });
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task UpdateSessionTimestampAsync(string chatId, CancellationToken ct)
    {
        ClawPilotDbContext db = dbFactory.CreateDbContext();
        await using (db)
        {
            ConversationMessage? row = await db.ConversationMessages.FirstOrDefaultAsync(m => m.TelegramChatId == chatId, ct);

            if (row is not null)
            {
                row.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task DeleteStoredSessionAsync(string chatId, CancellationToken ct)
    {
        ClawPilotDbContext db = dbFactory.CreateDbContext();
        await using (db)
        {
            ConversationMessage? row = await db.ConversationMessages
                .FirstOrDefaultAsync(m => m.TelegramChatId == chatId, ct);

            if (row is not null)
            {
                db.ConversationMessages.Remove(row);
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task<string> LoadSoulAsync()
    {
        // Search relative to executable, then working directory, then repo root
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Soul.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "Soul.md"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Soul.md")
        ];

        foreach (string path in candidates)
        {
            string resolved = Path.GetFullPath(path);
            if (File.Exists(resolved))
            {
                logger.LogInformation("Loaded Soul.md from {Path}", resolved);
                return await File.ReadAllTextAsync(resolved);
            }
        }

        logger.LogWarning("Soul.md not found — using default system prompt only");
        return string.Empty;
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();
}

