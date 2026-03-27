namespace ClawPilot.Worker.Options;

public class GitHubMcpOptions
{
    public string McpUrl { get; set; } = "https://api.githubcopilot.com/mcp/";
    public string Pat { get; set; } = string.Empty;
    public string[] Tools { get; set; } =
    [
        "get_file_contents", "list_pull_requests", "get_workflow_run",
        "get_workflow_run_logs", "create_issue", "push_files", "create_pull_request"
    ];
}
