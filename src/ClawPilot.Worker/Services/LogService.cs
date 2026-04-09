using System.Collections.Concurrent;
using System.Threading.Channels;
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
    // Prefix used internally to distinguish session-lifecycle events from log messages.
    internal const string SessionEndMarker = "\x00session.end";
    internal const string SessionErrorMarker = "\x00session.error";

    private readonly HubConnection _connection = new HubConnectionBuilder()
        .WithUrl(options.Value.Url)
        .WithAutomaticReconnect()
        .Build();

    private readonly ConcurrentDictionary<string, Channel<string>> _sseSubscribers = new();

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

    /// <summary>Register a new SSE client and return its message reader.</summary>
    public ChannelReader<string> Subscribe(string clientId)
    {
        Channel<string> channel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });
        _sseSubscribers[clientId] = channel;
        return channel.Reader;
    }

    /// <summary>Remove an SSE client and complete its channel.</summary>
    public void Unsubscribe(string clientId)
    {
        if (_sseSubscribers.TryRemove(clientId, out Channel<string>? channel))
            channel.Writer.TryComplete();
    }

    public async Task SendLogAsync(string message)
    {
        if (_connection.State == HubConnectionState.Connected)
            await _connection.InvokeAsync("SendLog", message);

        BroadcastToSseClients(message);
    }

    /// <summary>
    /// Signals all connected SSE clients that the current agent session has ended.
    /// Called by <see cref="CopilotService"/> at the end of every <c>RunAgentAsync</c> run.
    /// </summary>
    public Task BroadcastSessionEndAsync(bool success)
    {
        string marker = success ? SessionEndMarker : SessionErrorMarker;
        BroadcastToSseClients(marker);
        return Task.CompletedTask;
    }

    private void BroadcastToSseClients(string message)
    {
        foreach (Channel<string> channel in _sseSubscribers.Values)
            channel.Writer.TryWrite(message);
    }
}
