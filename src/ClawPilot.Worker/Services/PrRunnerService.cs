using System.Diagnostics;
using System.Text.Json;
using ClawPilot.Worker.Models;
using ClawPilot.Worker.Options;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Services;

/// <summary>
/// Dequeues <see cref="PrReviewJob"/> items produced by <see cref="PrWebhookService"/>,
/// spins a short-lived Docker container for each job, reads the structured result,
/// sends a diff to Telegram for human approval when fixes were applied, and
/// tears the container down regardless of outcome.
/// </summary>
public class PrRunnerService(
    PrWebhookService webhookService,
    TelegramService telegramService,
    IOptions<PrRunnerOptions> options,
    ILogger<PrRunnerService> logger) : BackgroundService
{
    private readonly PrRunnerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PR Runner Service started — waiting for jobs");
        await foreach (PrReviewJob job in webhookService.JobReader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error processing PR job {JobId}", job.JobId);
                await telegramService.SendMessageAsync(
                    $"❌ *PR Runner Error*\n\nJob `{EscapeMarkdown(job.JobId)}` faulted: {EscapeMarkdown(ex.Message)}",
                    stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(PrReviewJob job, CancellationToken ct)
    {
        logger.LogInformation("Starting PR job {JobId}: [{Source}] PR #{PrId} — {Title}",
            job.JobId, job.Source, job.PrId, job.PrTitle);

        await telegramService.SendMessageAsync(
            $"🔍 *PR Review Started*\n\n" +
            $"Source: `{EscapeMarkdown(job.Source)}`\n" +
            $"PR \\#{EscapeMarkdown(job.PrId)}: _{EscapeMarkdown(job.PrTitle)}_\n" +
            $"Branch: `{EscapeMarkdown(job.BranchName)}` → `{EscapeMarkdown(job.BaseBranch)}`",
            ct);

        string outputDir = Path.Combine(_options.WorkspaceRoot, job.JobId);
        string workspaceDir = Path.Combine(_options.WorkspaceRoot, job.JobId + "-workspace");
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(workspaceDir);

        try
        {
            PrRunnerResult result = await RunContainerAsync(job, outputDir, workspaceDir, ct);
            await HandleResultAsync(job, result, workspaceDir, ct);
        }
        finally
        {
            // Always clean up output and workspace directories
            try { Directory.Delete(outputDir, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove output dir {Dir}", outputDir); }
            try { Directory.Delete(workspaceDir, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove workspace dir {Dir}", workspaceDir); }
        }
    }

    // ─── Container execution ──────────────────────────────────────────────────

    private async Task<PrRunnerResult> RunContainerAsync(
        PrReviewJob job, string outputDir, string workspaceDir, CancellationToken ct)
    {
        string containerName = $"clawpilot-pr-{job.JobId[..8]}";

        // Build docker run arguments — PAT is injected via ProcessStartInfo.Environment,
        // never as a command-line flag (which would be visible in ps output / logs).
        List<string> dockerArgs = BuildDockerArgs(job, outputDir, workspaceDir, containerName);
        string argString = string.Join(" ", dockerArgs);

        logger.LogInformation("Spawning container {Name} for job {JobId}", containerName, job.JobId);

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = argString,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Inject PAT into the docker process environment so Docker reads it via
        // `--env=GIT_PAT` (no value in args = read from caller's env).  This
        // prevents the secret appearing in process listings or log lines.
        if (!string.IsNullOrEmpty(job.GitPat))
            psi.Environment["GIT_PAT"] = job.GitPat;

        using Process? process = Process.Start(psi);
        if (process is null)
            return FailedResult("Failed to start Docker process — is Docker installed and accessible?");

        using CancellationTokenSource timeoutCts =
            new CancellationTokenSource(TimeSpan.FromSeconds(_options.ContainerTimeoutSeconds));
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        string stdOut = string.Empty;
        string stdErr = string.Empty;

        try
        {
            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);
            stdOut = await stdOutTask;
            stdErr = await stdErrTask;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogWarning("Container {Name} timed out after {Timeout}s — killing",
                containerName, _options.ContainerTimeoutSeconds);
            await KillContainerAsync(containerName);
            return FailedResult($"Container timed out after {_options.ContainerTimeoutSeconds} seconds.");
        }

        if (process.ExitCode != 0 && !File.Exists(Path.Combine(outputDir, "result.json")))
        {
            logger.LogWarning("Container {Name} exited with code {Code}\nstdout: {Out}\nstderr: {Err}",
                containerName, process.ExitCode, stdOut, stdErr);
            return FailedResult($"Container exited with code {process.ExitCode}.\n{stdErr}");
        }

        return ReadResult(outputDir, stdOut, stdErr);
    }

    private static List<string> BuildDockerArgs(PrReviewJob job, string outputDir, string workspaceDir, string containerName)
    {
        List<string> args =
        [
            "run",
            "--rm",
            $"--name={containerName}",
            $"--volume={outputDir}:/output",
            $"--volume={workspaceDir}:/workspace",
            $"--env=REPO_URL={job.RepoUrl}",
            $"--env=BRANCH_NAME={job.BranchName}",
            $"--env=BASE_BRANCH={job.BaseBranch}",
            $"--env=PR_ID={job.PrId}",
        ];

        // GIT_PAT is read from the caller's process environment by Docker
        // when only the key (no =value) is specified — see ProcessStartInfo.Environment.
        if (!string.IsNullOrEmpty(job.GitPat))
            args.Add("--env=GIT_PAT");

        args.Add("--memory=2g");
        args.Add("--cpus=2");
        args.Add("claw-pilot-runner:latest"); // image last
        return args;
    }

    private static async Task KillContainerAsync(string containerName)
    {
        try
        {
            ProcessStartInfo killPsi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"kill {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process? kill = Process.Start(killPsi);
            if (kill is not null) await kill.WaitForExitAsync();
        }
        catch { /* best-effort */ }
    }

    private PrRunnerResult ReadResult(string outputDir, string stdOut, string stdErr)
    {
        string resultFile = Path.Combine(outputDir, "result.json");
        if (!File.Exists(resultFile))
        {
            logger.LogWarning("result.json not found in {Dir}", outputDir);
            string combinedLogs = string.Concat(stdOut, "\n", stdErr).Trim();
            return FailedResult("Runner did not produce result.json.\n" + combinedLogs);
        }

        try
        {
            string json = File.ReadAllText(resultFile);
            PrRunnerResult? result = JsonSerializer.Deserialize<PrRunnerResult>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result ?? FailedResult("result.json deserialized to null.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deserialize result.json");
            return FailedResult($"Failed to parse result.json: {ex.Message}");
        }
    }

    // ─── Result handling ──────────────────────────────────────────────────────

    private async Task HandleResultAsync(PrReviewJob job, PrRunnerResult result, string workspaceDir, CancellationToken ct)
    {
        string statusEmoji = result.Success ? "✅" : "❌";
        string buildMark = result.BuildPassed ? "✅" : "❌";
        string testMark = result.TestsPassed ? "✅" : "❌";
        string lintMark = result.LintPassed ? "✅" : "❌";

        string summary = $"{statusEmoji} *PR Review Complete*\n\n" +
                         $"PR \\#{EscapeMarkdown(job.PrId)}: _{EscapeMarkdown(job.PrTitle)}_\n\n" +
                         $"{buildMark} Build  {testMark} Tests  {lintMark} Lint\n\n" +
                         $"_{EscapeMarkdown(result.Summary)}_";

        await telegramService.SendMessageAsync(summary, ct);

        if (result.FixesApplied && !string.IsNullOrWhiteSpace(result.Diff))
        {
            bool approved = await telegramService.RequestDiffApprovalAsync(
                job.PrTitle, job.PrId, result.Diff, ct);

            if (approved)
            {
                logger.LogInformation("Diff approved for PR #{PrId} — pushing fixes", job.PrId);
                await PushFixesAsync(job, workspaceDir, ct);
            }
            else
            {
                logger.LogInformation("Diff rejected for PR #{PrId} — discarding fixes", job.PrId);
                await telegramService.SendMessageAsync(
                    $"🚫 Auto-fix commit discarded for PR \\#{EscapeMarkdown(job.PrId)}\\.", ct);
            }
        }
    }

    /// <summary>
    /// Pushes the auto-fix commit using a second short-lived container that
    /// mounts the same workspace written by the review container.
    /// Git credentials are injected via process environment — never as CLI args.
    /// </summary>
    private async Task PushFixesAsync(PrReviewJob job, string workspaceDir, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.GitPat))
        {
            await telegramService.SendMessageAsync(
                $"⚠️ Cannot push fixes for PR \\#{EscapeMarkdown(job.PrId)} — no Git PAT configured\\.",
                ct);
            return;
        }

        string pushOutputDir = Path.Combine(_options.WorkspaceRoot, job.JobId + "-push");
        Directory.CreateDirectory(pushOutputDir);

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"run --rm " +
                            $"--volume={pushOutputDir}:/output " +
                            $"--volume={workspaceDir}:/workspace " +
                            $"--env=REPO_URL={job.RepoUrl} " +
                            $"--env=BRANCH_NAME={job.BranchName} " +
                            $"--env=GIT_PAT " +
                            $"--env=PR_ID={job.PrId} " +
                            $"--env=PUSH_ONLY=true " +
                            $"--memory=512m " +
                            $"claw-pilot-runner:latest",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Inject PAT via process env so it is never visible in argument strings.
            psi.Environment["GIT_PAT"] = job.GitPat;

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                await telegramService.SendMessageAsync(
                    $"❌ Failed to start push container for PR \\#{EscapeMarkdown(job.PrId)}\\.", ct);
                return;
            }

            using CancellationTokenSource timeoutCts =
                new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await process.WaitForExitAsync(linked.Token);

            if (process.ExitCode == 0)
            {
                await telegramService.SendMessageAsync(
                    $"🚀 Auto-fix commit pushed to `{EscapeMarkdown(job.BranchName)}` for PR \\#{EscapeMarkdown(job.PrId)}\\.",
                    ct);
            }
            else
            {
                string err = await process.StandardError.ReadToEndAsync(ct);
                await telegramService.SendMessageAsync(
                    $"❌ Push failed for PR \\#{EscapeMarkdown(job.PrId)}: {EscapeMarkdown(err.Trim())}",
                    ct);
            }
        }
        finally
        {
            try { Directory.Delete(pushOutputDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static PrRunnerResult FailedResult(string reason) =>
        new PrRunnerResult(
            Success: false,
            BuildPassed: false,
            TestsPassed: false,
            LintPassed: false,
            FixesApplied: false,
            Diff: string.Empty,
            Summary: reason,
            Logs: string.Empty);

    private static string EscapeMarkdown(string text) =>
        text.Replace("_", "\\_")
            .Replace("*", "\\*")
            .Replace("`", "\\`")
            .Replace("[", "\\[")
            .Replace("#", "\\#")
            .Replace(".", "\\.")
            .Replace("!", "\\!");
}
