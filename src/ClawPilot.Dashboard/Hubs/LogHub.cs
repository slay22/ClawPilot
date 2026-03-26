using Microsoft.AspNetCore.SignalR;

namespace ClawPilot.Dashboard.Hubs;

public class LogHub : Hub
{
    public async Task SendLog(string message)
    {
        await Clients.All.SendAsync("ReceiveLog", message);
    }
}
