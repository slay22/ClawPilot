using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ClawPilot.Worker.Models;
using ClawPilot.Worker.Options;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Services;

/// <summary>
/// Listens for inbound PR webhook calls from GitHub or Azure DevOps,
/// validates the signature, parses the payload into a <see cref="PrReviewJob"/>,
/// and enqueues it for <see cref="PrRunnerService"/> to process.
/// </summary>
public class PrWebhookService(
    IOptions<PrRunnerOptions> options,
    ILogger<PrWebhookService> logger) : BackgroundService
{
    private readonly PrRunnerOptions _options = options.Value;
    private readonly Channel<PrReviewJob> _jobQueue = Channel.CreateUnbounded<PrReviewJob>();

    public ChannelReader<PrReviewJob> JobReader => _jobQueue.Reader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string prefix = $"http://+:{_options.WebhookPort}{_options.WebhookPath}/";
        using HttpListener listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
            logger.LogInformation("PR webhook listener started on {Prefix}", prefix);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start PR webhook listener on {Prefix}. " +
                "Ensure the process has permission to bind the port.", prefix);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error accepting webhook request");
                continue;
            }

            _ = HandleRequestAsync(context, stoppingToken);
        }

        listener.Stop();
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest req = context.Request;
        HttpListenerResponse resp = context.Response;

        try
        {
            if (!req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 405;
                resp.Close();
                return;
            }

            using StreamReader reader = new StreamReader(req.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync(ct);

            // Validate GitHub HMAC-SHA256 signature when secret is configured
            if (!string.IsNullOrEmpty(_options.WebhookSecret))
            {
                string? signature = req.Headers["X-Hub-Signature-256"];
                if (!ValidateGitHubSignature(body, signature, _options.WebhookSecret))
                {
                    logger.LogWarning("Webhook signature validation failed — request rejected");
                    resp.StatusCode = 401;
                    resp.Close();
                    return;
                }
            }

            string? githubEvent = req.Headers["X-GitHub-Event"];

            PrReviewJob? job = null;

            if (githubEvent != null)
                job = ParseGitHubPayload(body, githubEvent);
            else
                job = ParseAzureDevOpsPayload(body);

            if (job is null)
            {
                resp.StatusCode = 200; // ACK but ignore
                resp.Close();
                return;
            }

            logger.LogInformation("Enqueuing PR job {JobId} from {Source}: {Title}",
                job.JobId, job.Source, job.PrTitle);

            await _jobQueue.Writer.WriteAsync(job, ct);

            resp.StatusCode = 202;
            resp.Close();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing webhook payload");
            try
            {
                resp.StatusCode = 500;
                resp.Close();
            }
            catch { /* response already sent */ }
        }
    }

    // ─── GitHub ──────────────────────────────────────────────────────────────

    private PrReviewJob? ParseGitHubPayload(string body, string eventType)
    {
        if (!eventType.Equals("pull_request", StringComparison.OrdinalIgnoreCase))
            return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse GitHub webhook body as JSON");
            return null;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            string action = root.GetStringOrEmpty("action");

            // Only process opened / synchronize (new commits pushed) / reopened
            if (action is not ("opened" or "synchronize" or "reopened"))
                return null;

            JsonElement pr = root.GetProperty("pull_request");
            string prId = pr.GetProperty("number").ToString();
            string prTitle = pr.GetStringOrEmpty("title");
            string branchName = pr.GetProperty("head").GetStringOrEmpty("ref");
            string baseBranch = pr.GetProperty("base").GetStringOrEmpty("ref");
            string cloneUrl = pr.GetProperty("head").GetProperty("repo").GetStringOrEmpty("clone_url");

            if (string.IsNullOrEmpty(cloneUrl) || string.IsNullOrEmpty(branchName))
                return null;

            return new PrReviewJob(
                JobId: Guid.NewGuid().ToString("N"),
                Source: "GitHub",
                PrId: prId,
                PrTitle: prTitle,
                RepoUrl: cloneUrl,
                BranchName: branchName,
                BaseBranch: baseBranch,
                GitPat: _options.GitPat
            );
        }
    }

    // ─── Azure DevOps ─────────────────────────────────────────────────────────

    private PrReviewJob? ParseAzureDevOpsPayload(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse Azure DevOps webhook body as JSON");
            return null;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            string eventType = root.GetStringOrEmpty("eventType");

            // Only process git.pullrequest.created and git.pullrequest.updated
            if (eventType is not ("git.pullrequest.created" or "git.pullrequest.updated"))
                return null;

            JsonElement resource = root.GetProperty("resource");

            string prId = resource.GetProperty("pullRequestId").ToString();
            string prTitle = resource.GetStringOrEmpty("title");
            string sourceRef = resource.GetStringOrEmpty("sourceRefName"); // refs/heads/<branch>
            string targetRef = resource.GetStringOrEmpty("targetRefName");
            string remoteUrl = resource.GetProperty("repository").GetStringOrEmpty("remoteUrl");

            string branchName = sourceRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? sourceRef["refs/heads/".Length..]
                : sourceRef;

            string baseBranch = targetRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? targetRef["refs/heads/".Length..]
                : targetRef;

            if (string.IsNullOrEmpty(remoteUrl) || string.IsNullOrEmpty(branchName))
                return null;

            return new PrReviewJob(
                JobId: Guid.NewGuid().ToString("N"),
                Source: "AzureDevOps",
                PrId: prId,
                PrTitle: prTitle,
                RepoUrl: remoteUrl,
                BranchName: branchName,
                BaseBranch: baseBranch,
                GitPat: _options.GitPat
            );
        }
    }

    // ─── Signature validation ─────────────────────────────────────────────────

    private static bool ValidateGitHubSignature(string body, string? signature, string secret)
    {
        if (string.IsNullOrEmpty(signature)) return false;

        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

        using HMACSHA256 hmac = new HMACSHA256(keyBytes);
        byte[] hashBytes = hmac.ComputeHash(bodyBytes);
        string expected = "sha256=" + Convert.ToHexString(hashBytes).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature));
    }
}

// ─── JSON helpers ─────────────────────────────────────────────────────────────

internal static class JsonElementExtensions
{
    internal static string GetStringOrEmpty(this JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement prop) &&
            prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? string.Empty;
        return string.Empty;
    }
}
