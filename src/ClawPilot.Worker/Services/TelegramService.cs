using System.Collections.Concurrent;
using System.Threading.Channels;
using ClawPilot.Worker.Models;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClawPilot.Worker.Services;

public record TelegramCommand(string ChatId, string Text);

public class TelegramOptions
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
}

public class TelegramService(IOptions<TelegramOptions> options, ILogger<TelegramService> logger)
{
    private readonly TelegramOptions _options = options.Value;
    private readonly ITelegramBotClient _botClient = new TelegramBotClient(options.Value.BotToken);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
    private readonly Channel<TelegramCommand> _commandChannel = Channel.CreateUnbounded<TelegramCommand>();

    public ChannelReader<TelegramCommand> CommandReader => _commandChannel.Reader;

    public async Task StartReceivingAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Telegram Bot receiver...");
        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: new Telegram.Bot.Polling.ReceiverOptions { AllowedUpdates = [UpdateType.CallbackQuery, UpdateType.Message] },
            cancellationToken: stoppingToken
        );
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Telegram Bot Error");
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        switch (update)
        {
            case { CallbackQuery: { } callbackQuery }:
            {
                string? data = callbackQuery.Data;
                if (data == null) return;

                string[] parts = data.Split(':');
                if (parts.Length != 2) return;

                string action = parts[0];
                string requestId = parts[1];

                if (_pendingApprovals.TryRemove(requestId, out TaskCompletionSource<bool>? tcs))
                {
                    bool approved = action == "approve";
                    tcs.SetResult(approved);

                    await botClient.AnswerCallbackQuery(callbackQuery.Id, approved ? "Approved!" : "Denied!", cancellationToken: cancellationToken);
                    await botClient.EditMessageText(
                        callbackQuery.Message!.Chat.Id,
                        callbackQuery.Message.MessageId,
                        $"{callbackQuery.Message.Text}\n\nDecision: {(approved ? "✅ Approved" : "❌ Denied")}",
                        cancellationToken: cancellationToken
                    );
                }
                break;
            }
            case { Message: { } message }:
                logger.LogInformation("Received message from {ChatId}: {Text}", message.Chat.Id, message.Text);
                if (message.Text != null && message.Chat.Id.ToString() == _options.ChatId)
                    await _commandChannel.Writer.WriteAsync(new TelegramCommand(message.Chat.Id.ToString(), message.Text), cancellationToken);
                break;
        }
    }

    public async Task<bool> RequestPermissionAsync(string description, CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
        _pendingApprovals[requestId] = tcs;

        InlineKeyboardMarkup keyboard = new InlineKeyboardMarkup([[
            InlineKeyboardButton.WithCallbackData("✅ Approve", $"approve:{requestId}"),
            InlineKeyboardButton.WithCallbackData("❌ Deny", $"deny:{requestId}")
        ]]);

        await _botClient.SendMessage(
            chatId: _options.ChatId,
            text: $"🔔 *Permission Request*\n\n{description}",
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken
        );

        using CancellationTokenRegistration _ = cancellationToken.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    public async Task<bool> RequestPermissionTieredAsync(string toolName, string toolSource, string description, CancellationToken cancellationToken)
    {
        PermissionTier tier = DefaultPermissionPolicy.Resolve(toolName);

        if (tier == PermissionTier.AutoApprove)
        {
            logger.LogInformation("Auto-approving tool: {Tool}", toolName);
            return true;
        }

        if (tier == PermissionTier.AlwaysBlock)
        {
            await SendMessageAsync($"🚫 *Tool Blocked:* `{EscapeMarkdown(toolName)}`\n\n{EscapeMarkdown(description)}", cancellationToken);
            return false;
        }

        // RequireConfirmation with 60-second timeout
        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
        _pendingApprovals[requestId] = tcs;

        InlineKeyboardMarkup keyboard = new InlineKeyboardMarkup([[
            InlineKeyboardButton.WithCallbackData("✅ Approve", $"approve:{requestId}"),
            InlineKeyboardButton.WithCallbackData("❌ Deny", $"deny:{requestId}")
        ]]);

        await _botClient.SendMessage(
            chatId: _options.ChatId,
            text: $"🔔 *Permission Request*\n\n{description}",
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken
        );

        using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using CancellationTokenRegistration _ = linkedCts.Token.Register(() =>
        {
            if (_pendingApprovals.TryRemove(requestId, out TaskCompletionSource<bool>? removedTcs))
                removedTcs.TrySetResult(false);
        });

        return await tcs.Task;
    }

    public Task SendSessionOpenedAsync(string promptExcerpt, CancellationToken ct) =>
        SendMessageAsync($"🤖 *Session started*\n\n_{EscapeMarkdown(promptExcerpt.Length > 100 ? promptExcerpt[..100] + "…" : promptExcerpt)}_", ct);

    public Task SendSessionClosedAsync(int iterations, int corrections, TimeSpan elapsed, CancellationToken ct) =>
        SendMessageAsync($"✅ *Done* in {iterations} iteration(s) | 🔁 {corrections} correction(s) | ⏱ {elapsed:mm\\:ss}", ct);

    public Task SendBudgetExhaustedAsync(int iterations, CancellationToken ct) =>
        SendMessageAsync($"⚠️ *Budget Exhausted* after {iterations} iterations", ct);

    public Task SendSessionFaultedAsync(string reason, CancellationToken ct) =>
        SendMessageAsync($"❌ *Faulted:* {EscapeMarkdown(reason)}", ct);

    /// <summary>
    /// Sends a unified diff to Telegram as a code block and asks the operator
    /// to approve or reject the auto-fix commit.  The diff is truncated when it
    /// exceeds Telegram's 4 096-character message limit.
    /// </summary>
    public async Task<bool> RequestDiffApprovalAsync(
        string prTitle, string prId, string diff, CancellationToken cancellationToken)
    {
        const int maxDiffChars = 3500; // leave room for surrounding markup
        string truncated = diff.Length > maxDiffChars
            ? diff[..maxDiffChars] + "\n… (truncated)"
            : diff;

        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
        _pendingApprovals[requestId] = tcs;

        InlineKeyboardMarkup keyboard = new InlineKeyboardMarkup([[
            InlineKeyboardButton.WithCallbackData("✅ Push fixes", $"approve:{requestId}"),
            InlineKeyboardButton.WithCallbackData("❌ Discard", $"deny:{requestId}")
        ]]);

        string header = $"🔧 *Auto-fix diff for PR \\#{EscapeMarkdown(prId)}*: _{EscapeMarkdown(prTitle)}_\n\n";
        string codeBlock = $"```diff\n{truncated}\n```";

        await _botClient.SendMessage(
            chatId: _options.ChatId,
            text: header + codeBlock,
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken
        );

        using CancellationTokenRegistration _ = cancellationToken.Register(
            () => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    public async Task SendMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        await _botClient.SendMessage(
            chatId: _options.ChatId,
            text: text,
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken
        );
    }

    private static string EscapeMarkdown(string text) =>
        text.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[");
}
