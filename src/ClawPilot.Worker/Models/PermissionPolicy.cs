namespace ClawPilot.Worker.Models;

public enum PermissionTier { AutoApprove, RequireConfirmation, AlwaysBlock }

public record ToolPermission(string ToolName, string ToolSource, PermissionTier Tier, string? Reason = null);

public static class DefaultPermissionPolicy
{
    public static readonly IReadOnlyDictionary<string, PermissionTier> ByToolName =
        new Dictionary<string, PermissionTier>
        {
            ["dotnet_build"]             = PermissionTier.AutoApprove,
            ["get_schema"]               = PermissionTier.AutoApprove,
            ["execute_query"]            = PermissionTier.AutoApprove,
            ["get_file_contents"]        = PermissionTier.AutoApprove,
            ["list_pull_requests"]       = PermissionTier.AutoApprove,
            ["search_code"]              = PermissionTier.AutoApprove,
            ["get_workflow_run"]         = PermissionTier.AutoApprove,
            ["get_workflow_run_logs"]    = PermissionTier.AutoApprove,
            ["create_issue"]             = PermissionTier.RequireConfirmation,
            ["push_files"]               = PermissionTier.RequireConfirmation,
            ["create_pull_request"]      = PermissionTier.RequireConfirmation,
            ["web_search"]               = PermissionTier.RequireConfirmation,
            ["brave_web_search"]         = PermissionTier.RequireConfirmation,
            ["tavily-search"]            = PermissionTier.RequireConfirmation,
            ["tavily-extract"]           = PermissionTier.RequireConfirmation,
        };

    private static readonly HashSet<string> GitHubMcpTools = new HashSet<string>
    {
        "get_file_contents", "list_pull_requests", "search_code",
        "get_workflow_run", "get_workflow_run_logs", "create_issue",
        "push_files", "create_pull_request"
    };

    private static readonly HashSet<string> WebSearchMcpTools = new HashSet<string>
    {
        "web_search", "brave_web_search", "search", "tavily-search", "tavily-extract"
    };

    public static PermissionTier Resolve(string toolName) =>
        ByToolName.TryGetValue(toolName, out PermissionTier tier) ? tier : PermissionTier.RequireConfirmation;

    public static string ResolveSource(string toolName) =>
        GitHubMcpTools.Contains(toolName) ? ToolSources.GitHubMcp :
        WebSearchMcpTools.Contains(toolName) ? ToolSources.WebSearchMcp :
        ToolSources.Local;
}
