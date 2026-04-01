using ClawPilot.Worker.Data;
using ClawPilot.Worker.Models;
using ClawPilot.Worker.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Services;

public class AgentJournalService(
    IDbContextFactory<ClawPilotDbContext> dbFactory,
    IOptions<JournalOptions> journalOptions,
    ILogger<AgentJournalService> logger) : IAgentJournal
{
    private readonly JournalVerbosity _verbosity = journalOptions.Value.Verbosity;

    public async Task<AgentSession> OpenSessionAsync(string triggerSource, string prompt)
    {
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync();
        AgentSession session = new()
        {
            TriggerSource = triggerSource,
            InitialPrompt = prompt,
            Status = AgentSessionStatus.Running
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();
        logger.LogInformation("Opened session {Id} for trigger {Trigger}", session.Id, triggerSource);
        return session;
    }

    public async Task CloseSessionAsync(Guid sessionId, string status, string? finalSummary = null, string? faultReason = null)
    {
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync();
        AgentSession? session = await db.AgentSessions.FindAsync(sessionId);
        if (session is null) return;
        session.Status = status;
        session.EndedAt = DateTime.UtcNow;
        session.FinalSummary = finalSummary;
        session.FaultReason = faultReason;
        await db.SaveChangesAsync();
        logger.LogInformation("Closed session {Id} with status {Status}", sessionId, status);
    }

    public async Task AppendThoughtAsync(Guid sessionId, int seq, string reasoning)
    {
        if (_verbosity != JournalVerbosity.Full) return;
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync();
        db.ThoughtSteps.Add(new ThoughtStep { SessionId = sessionId, SequenceNumber = seq, ReasoningText = reasoning });
        await db.SaveChangesAsync();
    }

    public async Task AppendToolIntentAsync(Guid sessionId, int seq, string toolName, string toolSource, string argsJson, string? reasoningExcerpt = null)
    {
        if (_verbosity == JournalVerbosity.Minimal) return;
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync();
        db.ToolIntents.Add(new ToolIntent
        {
            SessionId = sessionId,
            SequenceNumber = seq,
            ToolName = toolName,
            ToolSource = toolSource,
            ArgumentsJson = argsJson,
            ReasoningExcerpt = reasoningExcerpt
        });
        await db.SaveChangesAsync();
    }

    public async Task<Guid> RecordToolIntentAsync(Guid sessionId, int seq, string toolName, string toolSource, string argsJson, string? reasoningExcerpt = null)
    {
        if (_verbosity == JournalVerbosity.Minimal) return Guid.Empty;
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync();
        ToolIntent intent = new ToolIntent
        {
            SessionId = sessionId,
            SequenceNumber = seq,
            ToolName = toolName,
            ToolSource = toolSource,
            ArgumentsJson = argsJson,
            ReasoningExcerpt = reasoningExcerpt
        };
        db.ToolIntents.Add(intent);
        await db.SaveChangesAsync();
        return intent.Id;
    }

    public async Task AppendToolOutcomeAsync(Guid toolIntentId, Guid sessionId, string permissionTier, bool approved, bool succeeded, long latencyMs, string? resultJson = null, string? errorMessage = null)
    {
        if (_verbosity == JournalVerbosity.Minimal) return;
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync();
        db.ToolOutcomes.Add(new ToolOutcome
        {
            ToolIntentId = toolIntentId,
            SessionId = sessionId,
            PermissionTier = permissionTier,
            Approved = approved,
            Succeeded = succeeded,
            LatencyMs = latencyMs,
            ResultJson = resultJson,
            ErrorMessage = errorMessage
        });
        await db.SaveChangesAsync();
    }

    public async Task AppendCorrectionAsync(Guid sessionId, int iteration, string failedApproach, string errorSignal, string revisedApproach)
    {
        if (_verbosity == JournalVerbosity.Minimal) return;
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync();
        db.CorrectionSteps.Add(new CorrectionStep
        {
            SessionId = sessionId,
            IterationNumber = iteration,
            FailedApproach = failedApproach,
            ErrorSignal = errorSignal,
            RevisedApproach = revisedApproach
        });
        await db.SaveChangesAsync();
    }

    public async Task WriteSessionSummaryAsync(Guid sessionId, string finalAnswer, int correctionsCount, int thoughtStepsCount, string budgetConsumedJson)
    {
        await using ClawPilotDbContext db = await dbFactory.CreateDbContextAsync();
        db.SessionSummaries.Add(new SessionSummary
        {
            SessionId = sessionId,
            FinalAnswer = finalAnswer,
            CorrectionsCount = correctionsCount,
            ThoughtStepsCount = thoughtStepsCount,
            BudgetConsumedJson = budgetConsumedJson
        });
        await db.SaveChangesAsync();
    }
}
