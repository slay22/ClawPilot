namespace ClawPilot.Worker.Options;

public class PrRunnerOptions
{
    /// <summary>Base container image used for PR review runs.</summary>
    public string ContainerImage { get; set; } = "claw-pilot-runner:latest";

    /// <summary>URL path the webhook listener binds to (e.g. /webhook/pr).</summary>
    public string WebhookPath { get; set; } = "/webhook/pr";

    /// <summary>TCP port the webhook listener binds to.</summary>
    public int WebhookPort { get; set; } = 9000;

    /// <summary>
    /// Optional HMAC-SHA256 secret used to validate GitHub webhook signatures.
    /// Leave empty to skip signature validation (not recommended for production).
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Host path where per-run output volumes are created.</summary>
    public string WorkspaceRoot { get; set; } = "/tmp/clawpilot-runs";

    /// <summary>Seconds to wait for the runner container before forcibly removing it.</summary>
    public int ContainerTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Default Git PAT injected into runner containers when the webhook payload
    /// does not supply one.  Sourced from <c>PrRunner:GitPat</c> config key.
    /// </summary>
    public string? GitPat { get; set; }
}
