using System.Diagnostics;
using System.Reflection;
using ClawPilot.Worker.Models;
using ClawPilot.Worker.Options;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Services;

public class GitHubOptions
{
    public string CopilotToken { get; set; } = string.Empty;
}

public class CopilotService(
    TelegramService telegramService,
    LogService logService,
    IAgentJournal journal,
    IOptions<AgentBudgetOptions> budgetOptions,
    ILogger<CopilotService> logger,
    IEnumerable<object> toolProviders,
    GitHubMcpService githubMcp,
    WebSearchMcpService webSearchMcp)
{
    private readonly AgentBudgetOptions _budget = budgetOptions.Value;
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
                    logger.LogInformation("Registering tool: {Name}", attr.Name);
                    yield return AIFunctionFactory.Create(method, provider, attr.Name, attr.Description);
                }
            }
        }
    }

    public async Task RunAgentAsync(string prompt, string triggerSource, CancellationToken stoppingToken)
    {
        AgentSession session = await journal.OpenSessionAsync(triggerSource, prompt);
        Guid sessionId = session.Id;
        int iterationsUsed = 0;
        int toolCallsUsed = 0;
        int toolSeq = 0;

        await telegramService.SendSessionOpenedAsync(prompt, stoppingToken);

        using CancellationTokenSource budgetCts = new CancellationTokenSource();
        budgetCts.CancelAfter(_budget.Timeout);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, budgetCts.Token);

        try
        {
            await _client.StartAsync();

            await using CopilotSession copilotSession = await _client.CreateSessionAsync(new SessionConfig
            {
                Model = "gpt-4o",
                Streaming = true,
                Tools = [.._tools, ..githubMcp.Tools, ..webSearchMcp.Tools],
                Hooks = new SessionHooks
                {
                    OnPreToolUse = async (input, _) =>
                    {
                        toolCallsUsed++;

                        if (toolCallsUsed >= _budget.MaxToolCallsPerSession)
                        {
                            logger.LogWarning("Tool call budget exhausted ({Max})", _budget.MaxToolCallsPerSession);
                            await budgetCts.CancelAsync();
                            return new PreToolUseHookOutput { PermissionDecision = "deny" };
                        }

                        string argsJson = input.ToolArgs?.ToString() ?? string.Empty;
                        string toolSource = DefaultPermissionPolicy.ResolveSource(input.ToolName);
                        Guid intentId = await journal.RecordToolIntentAsync(
                            sessionId, ++toolSeq, input.ToolName, toolSource, argsJson);

                        string description = $"Tool: **{input.ToolName}**\nArgs: `{argsJson}`";
                        Stopwatch sw = Stopwatch.StartNew();
                        bool approved = await telegramService.RequestPermissionTieredAsync(
                            input.ToolName, toolSource, description, linkedCts.Token);
                        sw.Stop();

                        await journal.AppendToolOutcomeAsync(
                            intentId, sessionId,
                            DefaultPermissionPolicy.Resolve(input.ToolName).ToString(),
                            approved, approved, sw.ElapsedMilliseconds);

                        logger.LogInformation("Tool {Tool} permission: {Decision}", input.ToolName, approved ? "approved" : "denied");

                        return new PreToolUseHookOutput { PermissionDecision = approved ? "allow" : "deny" };
                    }
                }
            });

            TaskCompletionSource done = new TaskCompletionSource();
            using CancellationTokenRegistration reg = linkedCts.Token.Register(() => done.TrySetCanceled());

            copilotSession.On(ev =>
            {
                switch (ev)
                {
                    case AssistantMessageDeltaEvent { Data.DeltaContent: string content }:
                        logService.SendLogAsync(content).Wait();
                        logger.LogDebug("Delta: {Content}", content);
                        break;

                    case ToolExecutionStartEvent { Data.ToolName: string toolName }:
                        logService.SendLogAsync($"🛠️ Calling tool: {toolName}").Wait();
                        logger.LogInformation("Calling tool: {Name}", toolName);
                        break;

                    case SessionIdleEvent:
                        iterationsUsed++;
                        if (iterationsUsed >= _budget.MaxIterations)
                        {
                            logger.LogWarning("Iteration budget exhausted ({Max})", _budget.MaxIterations);
                            budgetCts.Cancel();
                        }
                        else
                        {
                            done.TrySetResult();
                        }
                        break;

                    case SessionErrorEvent { Data.Message: string message }:
                        logger.LogError("Session error: {Message}", message);
                        done.TrySetException(new Exception(message));
                        break;
                }
            });

            logger.LogInformation("Sending prompt to Copilot...");
            await copilotSession.SendAsync(new MessageOptions { Prompt = prompt });
            await done.Task;

            string budgetJson = $"{{\"iterations\":{iterationsUsed},\"toolCalls\":{toolCallsUsed}}}";
            await journal.WriteSessionSummaryAsync(sessionId, "Completed", 0, 0, budgetJson);
            await journal.CloseSessionAsync(sessionId, AgentSessionStatus.Completed);

            TimeSpan elapsed = DateTime.UtcNow - session.StartedAt;
            await telegramService.SendSessionClosedAsync(iterationsUsed, 0, elapsed, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await journal.CloseSessionAsync(sessionId, AgentSessionStatus.Faulted, faultReason: "Host shutdown");
            throw;
        }
        catch (OperationCanceledException)
        {
            await journal.CloseSessionAsync(sessionId, AgentSessionStatus.BudgetExhausted);
            await telegramService.SendBudgetExhaustedAsync(iterationsUsed, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent session faulted");
            await journal.CloseSessionAsync(sessionId, AgentSessionStatus.Faulted, faultReason: ex.Message);
            await telegramService.SendSessionFaultedAsync(ex.Message, stoppingToken);
        }
        finally
        {
            await _client.StopAsync();
        }
    }
}
