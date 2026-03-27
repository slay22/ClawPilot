using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ClawPilot.Worker.Options;

namespace ClawPilot.Worker.Services;

public class GitHubMcpService(
    IOptions<GitHubMcpOptions> options,
    ILogger<GitHubMcpService> logger,
    ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private McpClient? _mcpClient;
    public IReadOnlyList<AIFunction> Tools { get; private set; } = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        GitHubMcpOptions opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.Pat))
        {
            logger.LogWarning("GitHub PAT not configured — GitHub MCP disabled");
            return;
        }

        try
        {
            HttpClientTransportOptions transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(opts.McpUrl),
                TransportMode = HttpTransportMode.Sse,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {opts.Pat}",
                    ["X-MCP-Tools"] = string.Join(",", opts.Tools),
                    ["github-mcp-lockdown"] = "true"
                }
            };

            _mcpClient = await McpClient.CreateAsync(
                new HttpClientTransport(transportOptions),
                clientOptions: null,
                loggerFactory: loggerFactory,
                cancellationToken: cancellationToken);

            IList<McpClientTool> mcpTools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            Tools = [..mcpTools.Cast<AIFunction>()];
            logger.LogInformation("GitHub MCP connected: {Count} tools registered", Tools.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to GitHub MCP server — continuing without GitHub tools");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_mcpClient is not null)
            await _mcpClient.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
            await _mcpClient.DisposeAsync();
    }
}
