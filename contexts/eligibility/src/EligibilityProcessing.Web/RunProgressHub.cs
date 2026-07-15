using Microsoft.AspNetCore.SignalR;

namespace EligibilityProcessing.Web;

/// <summary>
/// SignalR endpoint that dashboard clients connect to for live pipeline events.
/// The hub itself is just the connection terminal — the orchestrator pushes
/// events through <see cref="SignalRPipelineHooks"/>, which uses
/// <see cref="IHubContext{T}"/> to broadcast to all connected clients.
///
/// No client-callable methods are exposed today; subscriptions are passive.
/// Add Hub methods here when the dashboard wants to send commands back
/// (e.g. "subscribe to run X only").
/// </summary>
public sealed class RunProgressHub : Hub
{
}
