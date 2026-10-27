using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TradingEngine.Application;
using TradingEngine.Domain;
using TradingEngine.Infrastructure;

namespace TradingEngine.Api;

public sealed record MarketTick(string Symbol, decimal Price, decimal ChangePercent, decimal Volume, decimal High, decimal Low, decimal? Bid, decimal? Ask, DateTime UpdatedAt);

public sealed class BinanceMarketStream(
    IDbContextFactory<TradingDbContext> contexts,
    IDistributedCache cache,
    IHubContext<MarketHub> hub,
    MarketStreamStatus status,
    IConfiguration configuration,
    ILogger<BinanceMarketStream> logger) : BackgroundService
{
    private static readonly Uri StreamUri = new("wss://stream.binance.com:9443/ws/!miniTicker@arr");
    private HashSet<string> _watchedSymbols = [];
    private DateTime _lastWatchRefresh = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, DateTime> _lastCacheWrite = new();
    private decimal FeeRate => decimal.TryParse(configuration["Trading:FeeRate"], NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : .001m;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(StreamUri, stoppingToken);
                status.SetConnected(true);
                logger.LogInformation("Connected to Binance public ticker stream.");
                delay = TimeSpan.FromSeconds(1);
                var buffer = new byte[128 * 1024];
                while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, stoppingToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        message.Write(buffer, 0, result.Count);
                        if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("Binance ticker frame exceeded the maximum size.");
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    await HandleFrameAsync(message.ToArray(), stoppingToken);
                }
                if (!stoppingToken.IsCancellationRequested) throw new WebSocketException("Binance ticker stream disconnected.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                status.SetConnected(false);
                logger.LogWarning(ex, "Binance ticker stream failed; reconnecting after {Delay}.", delay);
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task HandleFrameAsync(byte[] payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        await RefreshWatchedSymbolsAsync(ct);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!TryRead(item, out var tick) || !tick.Symbol.EndsWith("USDT", StringComparison.Ordinal)) continue;
            try
            {
                status.MarkUpdate();
                if (!_lastCacheWrite.TryGetValue(tick.Symbol, out var lastWrite) || tick.UpdatedAt - lastWrite >= TimeSpan.FromMilliseconds(250))
                {
                    await cache.SetStringAsync($"market:ticker:{tick.Symbol}", JsonSerializer.Serialize(tick, new JsonSerializerOptions(JsonSerializerDefaults.Web)), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15) }, ct);
                    _lastCacheWrite[tick.Symbol] = tick.UpdatedAt;
                }
                await hub.Clients.Group(tick.Symbol).SendAsync("ticker", tick, ct);
                if (_watchedSymbols.Contains(tick.Symbol)) await EvaluateAlertsAndLimitOrdersAsync(tick, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to process ticker update for {Symbol}.", tick.Symbol);
            }
        }
    }

    private async Task RefreshWatchedSymbolsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastWatchRefresh < TimeSpan.FromSeconds(10)) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var alertSymbols = await db.PriceAlerts.AsNoTracking().Where(x => x.IsEnabled && x.TriggeredAt == null).Select(x => x.Symbol).Distinct().ToListAsync(ct);
        var orderSymbols = await db.Orders.AsNoTracking().Where(x => x.Status == OrderStatus.Open && x.Type == OrderType.Limit).Select(x => x.Symbol).Distinct().ToListAsync(ct);
        var marginSymbols = await db.MarginPositions.AsNoTracking().Where(x => x.Status == MarginPositionStatus.Open).Select(x => x.Symbol).Distinct().ToListAsync(ct);
        _watchedSymbols = alertSymbols.Concat(orderSymbols).Concat(marginSymbols).ToHashSet(StringComparer.Ordinal);
        _lastWatchRefresh = DateTime.UtcNow;
    }

    private async Task EvaluateAlertsAndLimitOrdersAsync(MarketTick tick, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var alerts = await db.PriceAlerts.Where(x => x.Symbol == tick.Symbol && x.IsEnabled && x.TriggeredAt == null).ToListAsync(ct);
        foreach (var alert in alerts)
        {
            var crossed = alert.Condition == AlertCondition.Above ? tick.Price >= alert.TargetPrice : tick.Price <= alert.TargetPrice;
            if (!crossed) continue;
            alert.IsEnabled = false;
            alert.TriggeredAt = tick.UpdatedAt;
            db.Notifications.Add(new AppNotification { AccountId = alert.AccountId, Type = "PriceAlertTriggered", Message = $"{alert.Symbol} reached {tick.Price:N8} (alert {alert.Condition} {alert.TargetPrice:N8}).", CreatedAt = tick.UpdatedAt });
            await hub.Clients.Group($"user:{alert.AccountId}").SendAsync("notification", new { type = "PriceAlertTriggered", alert.Symbol, price = tick.Price }, ct);
        }

        var marginPositions = await db.MarginPositions.AsNoTracking().Where(x => x.Symbol == tick.Symbol && x.Status == MarginPositionStatus.Open).ToListAsync(ct);
        foreach (var position in marginPositions)
        {
            await using var marginTx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.MarginPositions.FromSqlInterpolated($"SELECT * FROM \"MarginPositions\" WHERE \"Id\" = {position.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (current is null) { await marginTx.RollbackAsync(ct); continue; }
            if (current.Status != MarginPositionStatus.Open) { await marginTx.RollbackAsync(ct); continue; }
            var liquidated = current.Side == OrderSide.Buy ? tick.Price <= current.LiquidationPrice : tick.Price >= current.LiquidationPrice;
            var stopped = !liquidated && current.StopLoss is not null && (current.Side == OrderSide.Buy ? tick.Price <= current.StopLoss : tick.Price >= current.StopLoss);
            var tookProfit = !liquidated && !stopped && current.TakeProfit is not null && (current.Side == OrderSide.Buy ? tick.Price >= current.TakeProfit : tick.Price <= current.TakeProfit);
            if (!liquidated && !stopped && !tookProfit) { await marginTx.RollbackAsync(ct); continue; }
            var account = await db.Accounts.SingleAsync(x => x.Id == current.AccountId, ct);
            var gross = (current.Side == OrderSide.Buy ? tick.Price - current.EntryPrice : current.EntryPrice - tick.Price) * current.Quantity;
            var exitFee = liquidated ? 0m : TradingRules.Fee(current.Quantity * tick.Price, FeeRate);
            var preFeeRelease = liquidated ? 0m : Math.Max(0m, current.Collateral + gross);
            var paidExitFee = Math.Min(preFeeRelease, exitFee);
            var returned = preFeeRelease - paidExitFee;
            current.Status = liquidated ? MarginPositionStatus.Liquidated : stopped ? MarginPositionStatus.StoppedOut : MarginPositionStatus.TakeProfit;
            current.ExitPrice = tick.Price; current.RealizedPnl = (liquidated ? -current.Collateral : gross) - current.EntryFee - paidExitFee; current.ClosedAt = tick.UpdatedAt;
            account.UsdBalance += returned;
            db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.Adjustment, Amount = preFeeRelease, BalanceAfter = account.UsdBalance + paidExitFee, Description = $"{current.Status} simulated margin position {current.Symbol}", CreatedAt = tick.UpdatedAt });
            if (paidExitFee > 0) db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.Fee, Amount = -paidExitFee, BalanceAfter = account.UsdBalance, Description = "Simulated leveraged closing fee", CreatedAt = tick.UpdatedAt });
            var message = $"{current.Symbol} {current.Status} at {tick.Price:N4}; realized P&L {current.RealizedPnl:+0.00;-0.00}.";
            db.Notifications.Add(new AppNotification { AccountId = account.Id, Type = current.Status.ToString(), Message = message, CreatedAt = tick.UpdatedAt });
            await hub.Clients.Group($"user:{account.Id}").SendAsync("notification", new { type = current.Status.ToString(), current.Symbol, price = tick.Price, current.RealizedPnl }, ct);
            await db.SaveChangesAsync(ct); await marginTx.CommitAsync(ct);
        }

        var orders = await db.Orders.Where(x => x.Symbol == tick.Symbol && x.Status == OrderStatus.Open && x.Type == OrderType.Limit).ToListAsync(ct);
        foreach (var order in orders)
        {
            var shouldFill = order.LimitPrice is not null && TradingRules.ShouldExecuteLimit(order.Side, tick.Price, order.LimitPrice.Value);
            if (!shouldFill) continue;
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var current = await db.Orders.SingleAsync(x => x.Id == order.Id, ct);
            if (current.Status != OrderStatus.Open) { await tx.RollbackAsync(ct); continue; }
            var account = await db.Accounts.SingleAsync(x => x.Id == current.AccountId, ct);
            if (account.Status != AccountStatus.Active) { await ReleaseOrderAsync(db, current, account, ct); current.Status = OrderStatus.Cancelled; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); continue; }
            var asset = await db.Assets.SingleOrDefaultAsync(x => x.AccountId == current.AccountId && x.Asset == current.BaseAsset, ct);
            if (current.Side == OrderSide.Buy && asset is null)
            {
                asset = new AssetBalance { AccountId = current.AccountId, Asset = current.BaseAsset };
                db.Assets.Add(asset);
            }
            if (asset is null) { await ReleaseOrderAsync(db, current, account, ct); current.Status = OrderStatus.Cancelled; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); continue; }
            var notional = decimal.Round(current.Quantity * tick.Price, 8);
            var fee = TradingRules.Fee(notional, FeeRate);
            current.FillPrice = tick.Price; current.Fee = fee; current.FilledAt = tick.UpdatedAt; current.Status = OrderStatus.Filled;
            if (current.Side == OrderSide.Buy)
            {
                var reserve = current.ReservedAmount;
                db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.OrderRelease, Amount = reserve, BalanceAfter = account.UsdBalance + reserve, Description = "Limit order reserve released on execution", OrderId = current.Id, CreatedAt = tick.UpdatedAt });
                account.UsdBalance += reserve - notional - fee;
                (asset.Quantity, asset.AverageCost) = TradingRules.AddToPosition(asset.Quantity, asset.AverageCost, current.Quantity, tick.Price);
                db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.Buy, Amount = -notional, BalanceAfter = account.UsdBalance + notional + fee - notional, Description = $"Limit buy {current.Quantity} {current.BaseAsset} at {tick.Price}", OrderId = current.Id, CreatedAt = tick.UpdatedAt });
                if (fee > 0) db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.Fee, Amount = -fee, BalanceAfter = account.UsdBalance, Description = "Simulated trading fee", OrderId = current.Id, CreatedAt = tick.UpdatedAt });
                current.ReservedAmount = 0;
            }
            else
            {
                asset.Quantity -= current.Quantity; asset.ReservedQuantity -= current.Quantity;
                account.UsdBalance += notional - fee;
                current.RealizedPnl = TradingRules.RealizedPnl(current.Quantity, asset.AverageCost, tick.Price, fee);
                db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.Sell, Amount = notional, BalanceAfter = account.UsdBalance + fee, Description = $"Limit sell {current.Quantity} {current.BaseAsset} at {tick.Price}", OrderId = current.Id, CreatedAt = tick.UpdatedAt });
                if (fee > 0) db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.Fee, Amount = -fee, BalanceAfter = account.UsdBalance, Description = "Simulated trading fee", OrderId = current.Id, CreatedAt = tick.UpdatedAt });
            }
            db.Notifications.Add(new AppNotification { AccountId = account.Id, Type = "OrderFilled", Message = $"Limit {current.Side} {current.Symbol} filled at {tick.Price:N8}.", CreatedAt = tick.UpdatedAt });
            await hub.Clients.Group($"user:{account.Id}").SendAsync("notification", new { type = "OrderFilled", current.Symbol, price = tick.Price }, ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    private static async Task ReleaseOrderAsync(TradingDbContext db, TradingOrder order, TradingAccount account, CancellationToken ct)
    {
        if (order.Side == OrderSide.Buy && order.ReservedAmount > 0)
        {
            account.UsdBalance += order.ReservedAmount;
            db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.OrderRelease, Amount = order.ReservedAmount, BalanceAfter = account.UsdBalance, Description = "Limit buy reserve released", OrderId = order.Id });
            order.ReservedAmount = 0;
        }
        else if (order.Side == OrderSide.Sell)
        {
            var asset = await db.Assets.SingleAsync(x => x.AccountId == order.AccountId && x.Asset == order.BaseAsset, ct);
            asset.ReservedQuantity -= order.Quantity;
        }
    }

    private static bool TryRead(JsonElement item, out MarketTick tick)
    {
        tick = default!;
        try
        {
            var symbol = item.GetProperty("s").GetString()!;
            decimal D(string key, decimal fallback = 0) => item.TryGetProperty(key, out var value) && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
            decimal? Optional(string key) => item.TryGetProperty(key, out var value) && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            var price = D("c"); var open = D("o", price); var change = item.TryGetProperty("P", out var pct) && decimal.TryParse(pct.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPct) ? parsedPct : open == 0 ? 0 : (price - open) / open * 100;
            var updatedAt = item.TryGetProperty("E", out var eventTime) && eventTime.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;
            tick = new MarketTick(symbol, price, change, D("q"), D("h"), D("l"), Optional("b"), Optional("a"), updatedAt);
            return tick.Price > 0;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException or NullReferenceException) { return false; }
    }
}
