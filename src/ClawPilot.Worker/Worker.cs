using ClawPilot.Worker.Services;

namespace ClawPilot.Worker;

public class Worker(
    TelegramService telegramService,
    CopilotService copilotService,
    LogService logService,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ClawPilot Worker starting...");

        _ = logService.StartAsync(stoppingToken);
        _ = telegramService.StartReceivingAsync(stoppingToken);

        await telegramService.SendMessageAsync("🤖 *Claw-Pilot Agent Online*", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string prompt = await telegramService.CommandReader.ReadAsync(stoppingToken);

                logger.LogInformation("Processing prompt: {Prompt}", prompt);

                await copilotService.RunAgentAsync(prompt, "Telegram", stoppingToken);
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
