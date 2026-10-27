using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace TradingEngine.Api;

public sealed class MarketStreamStatus
{
    private long _lastUpdateTicks;
    private int _connected;
    public void SetConnected(bool connected) => Interlocked.Exchange(ref _connected, connected ? 1 : 0);
    public void MarkUpdate() => Interlocked.Exchange(ref _lastUpdateTicks, DateTime.UtcNow.Ticks);
    public bool IsFresh => Interlocked.Read(ref _lastUpdateTicks) > DateTime.UtcNow.AddSeconds(-15).Ticks;
    public bool IsConnected => Volatile.Read(ref _connected) == 1 && IsFresh;
}

[Authorize]
public sealed class MarketHub : Hub
{
    public async Task JoinMarket(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (symbol.Length is < 5 or > 20 || !symbol.EndsWith("USDT", StringComparison.Ordinal))
            throw new HubException("Unsupported market symbol.");
        await Groups.AddToGroupAsync(Context.ConnectionId, symbol);
    }

    public Task LeaveMarket(string symbol) => Groups.RemoveFromGroupAsync(Context.ConnectionId, symbol.Trim().ToUpperInvariant());

    public override async Task OnConnectedAsync()
    {
        var accountId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (accountId is not null) await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{accountId}");
        await base.OnConnectedAsync();
    }
}
