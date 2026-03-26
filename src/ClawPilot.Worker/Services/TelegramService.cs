using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Worker.Services;

public class TelegramOptions
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
}

public class TelegramService
{
    private readonly ITelegramBotClient _botClient;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramService> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
    private readonly Channel<string> _commandChannel = Channel.CreateUnbounded<string>();

    public TelegramService(IOptions<TelegramOptions> options, ILogger<TelegramService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _botClient = new TelegramBotClient(_options.BotToken);
    }

    public ChannelReader<string> CommandReader => _commandChannel.Reader;

    public async Task StartReceivingAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Telegram Bot receiver...");
        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: new Telegram.Bot.Polling.ReceiverOptions { AllowedUpdates = [UpdateType.CallbackQuery, UpdateType.Message] },
            cancellationToken: stoppingToken
        );
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram Bot Error");
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is { } callbackQuery)
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
        }
        else if (update.Message is { } message)
        {
            _logger.LogInformation("Received message from {chatId}: {text}", message.Chat.Id, message.Text);
            if (message.Text != null && message.Chat.Id.ToString() == _options.ChatId)
            {
                await _commandChannel.Writer.WriteAsync(message.Text, cancellationToken);
            }
        }
    }

    public async Task<bool> RequestPermissionAsync(string description, CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
        _pendingApprovals[requestId] = tcs;

        InlineKeyboardMarkup keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Approve", $"approve:{requestId}"),
                InlineKeyboardButton.WithCallbackData("❌ Deny", $"deny:{requestId}")
            }
        });

        await _botClient.SendMessage(
            chatId: _options.ChatId,
            text: $"🔔 *Permission Request*\n\n{description}",
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken
        );

        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
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
}
