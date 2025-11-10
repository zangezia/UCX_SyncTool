using Microsoft.AspNetCore.SignalR;

namespace UCX.SyncTool.Web.Hubs;

/// <summary>
/// SignalR hub for real-time synchronization updates
/// </summary>
public class SyncHub : Hub
{
    public async Task SendLog(string message)
    {
        await Clients.All.SendAsync("ReceiveLog", message);
    }

    public async Task UpdateStatus(object status)
    {
        await Clients.All.SendAsync("ReceiveStatus", status);
    }

    public async Task UpdateProgress(string node, string share, int progress)
    {
        await Clients.All.SendAsync("ReceiveProgress", node, share, progress);
    }
}
