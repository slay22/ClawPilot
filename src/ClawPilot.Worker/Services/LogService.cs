using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Services;

public class DashboardOptions
{
    public string Url { get; set; } = "http://localhost:5247/logHub";
}

public class LogService
{
    private readonly HubConnection _connection;
    private readonly ILogger<LogService> _logger;

    public LogService(IOptions<DashboardOptions> options, ILogger<LogService> logger)
    {
        _logger = logger;
        _connection = new HubConnectionBuilder()
            .WithUrl(options.Value.Url)
            .WithAutomaticReconnect()
            .Build();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await _connection.StartAsync(ct);
            _logger.LogInformation("SignalR LogService started.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to LogHub");
        }
    }

    public async Task SendLogAsync(string message)
    {
        if (_connection.State == HubConnectionState.Connected)
        {
            await _connection.InvokeAsync("SendLog", message);
        }
    }
}
