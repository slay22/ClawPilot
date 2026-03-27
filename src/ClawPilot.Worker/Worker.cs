using ClawPilot.Worker.Services;

namespace ClawPilot.Worker;

public class Worker(
    TelegramService telegramService,
    CopilotService copilotService,
    ConversationService conversationService,
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
                "🤖 *Claw\\-Pilot Agent Online*\n\nSend a message to chat, or use `/task <prompt>` to run a task\\.",
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
}
