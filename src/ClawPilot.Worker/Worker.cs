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

        // Start LogService
        _ = logService.StartAsync(stoppingToken);

        // Start Telegram receiver
        _ = telegramService.StartReceivingAsync(stoppingToken);

        await telegramService.SendMessageAsync("🤖 *Claw-Pilot Agent Online*", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for commands from Telegram
                string prompt = await telegramService.CommandReader.ReadAsync(stoppingToken);

                logger.LogInformation("Processing prompt: {prompt}", prompt);
                await telegramService.SendMessageAsync($"🧠 *Thinking...*\n\nProcessing: `{prompt}`", stoppingToken);

                // Start Agentic Loop
                await copilotService.RunAgentAsync(prompt, stoppingToken);

                await telegramService.SendMessageAsync("✅ *Task Complete*", stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in agent loop");
                await telegramService.SendMessageAsync($"❌ *Error:*\n\n{ex.Message}", stoppingToken);
            }
        }
    }
}
