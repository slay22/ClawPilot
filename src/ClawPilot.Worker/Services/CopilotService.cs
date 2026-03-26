using System.Reflection;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using ClawPilot.Worker.Models;

namespace ClawPilot.Worker.Services;

public class GitHubOptions
{
    public string CopilotToken { get; set; } = string.Empty;
}

public class CopilotService(
    TelegramService telegramService,
    LogService logService,
    AgentJournal journal,
    ILogger<CopilotService> logger,
    IEnumerable<object> toolProviders)
{
    private readonly CopilotClient _client = new CopilotClient();
    private readonly List<AIFunction> _tools = [..BuildTools(toolProviders, logger)];

    private static IEnumerable<AIFunction> BuildTools(IEnumerable<object> providers, ILogger logger)
    {
        foreach (object provider in providers)
        {
            MethodInfo[] methods = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            foreach (MethodInfo method in methods)
            {
                if (method.GetCustomAttribute<CopilotToolAttribute>() is { } attr)
                {
                    logger.LogInformation("Registering tool: {name}", attr.Name);
                    yield return AIFunctionFactory.Create(method, provider, attr.Name, attr.Description);
                }
            }
        }
    }

    public async Task RunAgentAsync(string prompt, CancellationToken stoppingToken)
    {
        journal.AddEntry($"Starting agent loop for prompt: {prompt}");

        await _client.StartAsync();

        await using CopilotSession session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = "gpt-4o",
            Streaming = true,
            Tools = [.._tools],
            Hooks = new SessionHooks
            {
                OnPreToolUse = async (input, _) =>
                {
                    string description = $"Tool: **{input.ToolName}**\nArgs: `{input.ToolArgs}`";
                    logger.LogInformation("Permission requested for tool: {tool}", input.ToolName);
                    journal.AddEntry($"Permission requested for: {input.ToolName}", action: "OnPreToolUse");

                    bool approved = await telegramService.RequestPermissionAsync(description, stoppingToken);

                    journal.AddEntry(
                        $"Permission {(approved ? "approved" : "denied")}: {input.ToolName}",
                        result: approved.ToString());

                    return new PreToolUseHookOutput
                    {
                        PermissionDecision = approved ? "allow" : "deny"
                    };
                }
            }
        });

        TaskCompletionSource done = new TaskCompletionSource();
        using CancellationTokenRegistration _ = stoppingToken.Register(() => done.TrySetCanceled());

        session.On(ev =>
        {
            switch (ev)
            {
                case AssistantMessageDeltaEvent { Data.DeltaContent: var content }:
                    logService.SendLogAsync(content).Wait();
                    logger.LogDebug("Delta: {content}", content);
                    break;

                case ToolExecutionStartEvent { Data.ToolName: var toolName }:
                    logService.SendLogAsync($"🛠️ Calling tool: {toolName}").Wait();
                    logger.LogInformation("Calling tool: {name}", toolName);
                    journal.AddEntry($"Calling tool: {toolName}", action: toolName);
                    break;

                case SessionIdleEvent:
                    done.TrySetResult();
                    break;

                case SessionErrorEvent { Data.Message: var message }:
                    logger.LogError("Session error: {msg}", message);
                    done.TrySetException(new Exception(message));
                    break;
            }
        });

        logger.LogInformation("Sending prompt to Copilot...");
        await session.SendAsync(new MessageOptions { Prompt = prompt });
        await done.Task;

        journal.AddEntry("Agent loop finished.");
        await _client.StopAsync();
    }
}
