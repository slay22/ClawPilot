namespace ClawPilot.Worker.Models;

/// <summary>Represents a PR review job dequeued by the webhook service.</summary>
public record PrReviewJob(
    string JobId,
    string Source,       // "GitHub" | "AzureDevOps" | "Generic"
    string PrId,
    string PrTitle,
    string RepoUrl,
    string BranchName,
    string BaseBranch,
    string? GitPat       // short-lived PAT injected at runtime — never stored
);

/// <summary>Structured output written by the runner container to /output/result.json.</summary>
public record PrRunnerResult(
    bool Success,
    bool BuildPassed,
    bool TestsPassed,
    bool LintPassed,
    bool FixesApplied,
    string Diff,
    string Summary,
    string Logs
);
