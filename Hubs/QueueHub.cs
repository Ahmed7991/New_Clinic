using ClinicApi.DTOs;
using Microsoft.AspNetCore.SignalR;

namespace ClinicApi.Hubs;

/// <summary>
/// Real-time hub for secretary and doctor dashboards.
/// Clients join the "queue" group to receive live updates.
/// </summary>
public class QueueHub : Hub
{
    // ── Client Methods (called FROM server TO clients) ─────────────────────────
    // Clients listen for:
    //   "QueueUpdated"       → full queue refresh (list of QueueItemDto)
    //   "StatusChanged"      → single appointment status change
    //   "NewBooking"         → new appointment added
    //   "DoctorViewUpdated"  → current patient + up-next (DoctorViewDto)

    public override async Task OnConnectedAsync()
    {
        // All dashboard clients join a shared group
        await Groups.AddToGroupAsync(Context.ConnectionId, "queue");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "queue");
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Helper to broadcast queue events from services/controllers.
/// Inject IHubContext<QueueHub> and use this extension.
/// </summary>
public static class QueueHubExtensions
{
    public static async Task BroadcastQueueUpdate(
        this IHubContext<QueueHub> hub, IEnumerable<QueueItemDto> queue)
    {
        await hub.Clients.Group("queue").SendAsync("QueueUpdated", queue);
    }

    public static async Task BroadcastStatusChange(
        this IHubContext<QueueHub> hub, int appointmentId, string newStatus)
    {
        await hub.Clients.Group("queue")
            .SendAsync("StatusChanged", new { appointmentId, newStatus });
    }

    public static async Task BroadcastNewBooking(
        this IHubContext<QueueHub> hub, BookingResponse booking)
    {
        await hub.Clients.Group("queue").SendAsync("NewBooking", booking);
    }

    public static async Task BroadcastDoctorView(
        this IHubContext<QueueHub> hub, DoctorViewDto view)
    {
        await hub.Clients.Group("queue").SendAsync("DoctorViewUpdated", view);
    }
}
