using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClawPilot.Worker.Services;

public class DashboardOptions
{
    public string Url { get; set; } = "http://localhost:5247/logHub";
}

public class LogService(IOptions<DashboardOptions> options, ILogger<LogService> logger)
{
    private readonly HubConnection _connection = new HubConnectionBuilder()
        .WithUrl(options.Value.Url)
        .WithAutomaticReconnect()
        .Build();

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await _connection.StartAsync(ct);
            logger.LogInformation("SignalR LogService started.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to LogHub");
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
