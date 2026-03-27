using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace ClawPilot.Worker.Services;

public class WebSearchMcpOptions
{
    public string TavilyApiKey { get; set; } = string.Empty;
}

public class WebSearchMcpService(
    IOptions<WebSearchMcpOptions> options,
    ILogger<WebSearchMcpService> logger,
    ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private McpClient? _mcpClient;
    public IReadOnlyList<AIFunction> Tools { get; private set; } = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        WebSearchMcpOptions opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.TavilyApiKey))
        {
            logger.LogWarning("Tavily API key not configured — WebSearch MCP disabled");
            return;
        }

        try
        {
            HttpClientTransportOptions transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri($"https://mcp.tavily.com/mcp/?tavilyApiKey={opts.TavilyApiKey}"),
                TransportMode = HttpTransportMode.StreamableHttp
            };

            _mcpClient = await McpClient.CreateAsync(
                new HttpClientTransport(transportOptions),
                clientOptions: null,
                loggerFactory: loggerFactory,
                cancellationToken: cancellationToken);

            IList<McpClientTool> mcpTools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
            Tools = [..mcpTools.Cast<AIFunction>()];
            logger.LogInformation("WebSearch MCP connected: {Count} tools", Tools.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to Tavily MCP server — web search unavailable");
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
