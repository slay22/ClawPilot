using ClawPilot.Worker.Models;

namespace ClawPilot.Worker.Services;

public interface IAgentJournal
{
    Task<AgentSession> OpenSessionAsync(string triggerSource, string prompt);
    Task CloseSessionAsync(Guid sessionId, string status, string? finalSummary = null, string? faultReason = null);
    Task AppendThoughtAsync(Guid sessionId, int seq, string reasoning);
    Task AppendToolIntentAsync(Guid sessionId, int seq, string toolName, string toolSource, string argsJson, string? reasoningExcerpt = null);
    Task<Guid> RecordToolIntentAsync(Guid sessionId, int seq, string toolName, string toolSource, string argsJson, string? reasoningExcerpt = null);
    Task AppendToolOutcomeAsync(Guid toolIntentId, Guid sessionId, string permissionTier, bool approved, bool succeeded, long latencyMs, string? resultJson = null, string? errorMessage = null);
    Task AppendCorrectionAsync(Guid sessionId, int iteration, string failedApproach, string errorSignal, string revisedApproach);
    Task WriteSessionSummaryAsync(Guid sessionId, string finalAnswer, int correctionsCount, int thoughtStepsCount, string budgetConsumedJson);
}
