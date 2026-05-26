using Microsoft.AspNetCore.SignalR;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Hubs;

public class TradeHub : Hub
{
    private readonly ILogger<TradeHub> _logger;

    public TradeHub(ILogger<TradeHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SubscribeToSymbol(string symbol)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, symbol);
        _logger.LogInformation("Client {ConnectionId} subscribed to {Symbol}", Context.ConnectionId, symbol);
    }

    public async Task UnsubscribeFromSymbol(string symbol)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, symbol);
    }
}
