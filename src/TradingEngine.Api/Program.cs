using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TradingEngine.Application;
using TradingEngine.Domain;
using TradingEngine.Infrastructure;
using TradingEngine.Api;

var builder = WebApplication.CreateBuilder(args);
var jwtKey = builder.Configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey must be configured.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
builder.Services.AddDbContextFactory<TradingDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("TradingDb") ?? "Host=localhost;Database=tradingengine;Username=postgres;Password=postgres"));
builder.Services.AddScoped<IPasswordHasher<TradingAccount>, PasswordHasher<TradingAccount>>();
builder.Services.AddHttpClient("binance", c => { c.BaseAddress = new Uri("https://api.binance.com"); c.Timeout = TimeSpan.FromSeconds(10); });
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<MarketStreamStatus>();
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrWhiteSpace(redisConnection)) builder.Services.AddDistributedMemoryCache();
else builder.Services.AddStackExchangeRedisCache(o => { o.Configuration = redisConnection; o.InstanceName = "TradingEngine:"; });
var signalR = builder.Services.AddSignalR();
if (!string.IsNullOrWhiteSpace(redisConnection)) signalR.AddStackExchangeRedis(redisConnection);
builder.Services.AddHostedService<BinanceMarketStream>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
    o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "TradingEngine", ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"] ?? "TradingEngine.Web", ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)), ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30) };
    o.Events = new JwtBearerEvents { OnMessageReceived = context => { var token = context.Request.Query["access_token"]; if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/market")) context.Token = token; return Task.CompletedTask; } };
});
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"]).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("api", x => { x.PermitLimit = 120; x.Window = TimeSpan.FromMinutes(1); x.QueueLimit = 0; }));
builder.Services.AddOpenApi();
var app = builder.Build();
// Apply schema migrations before serving requests or starting background workers.
await using (var migrationScope = app.Services.CreateAsyncScope())
{
    var dbFactory = migrationScope.ServiceProvider.GetRequiredService<IDbContextFactory<TradingDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
app.UseRateLimiter(); app.UseCors(); app.UseAuthentication(); app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapHub<MarketHub>("/hubs/market").RequireAuthorization();
app.MapGet("/api/market-status", (MarketStreamStatus status) => Results.Ok(new { connected = status.IsConnected, fresh = status.IsFresh, provider = "Binance public WebSocket", simulation = true })).AllowAnonymous();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", simulation = true }));

app.MapPost("/api/auth/register", async (RegisterRequest request, TradingDbContext db, IPasswordHasher<TradingAccount> hasher, IConfiguration config, HttpContext context) => {
    var email = request.Email.Trim().ToLowerInvariant(); var username = request.Username.Trim();
    if (email.Length > 320 || !email.Contains('@') || username.Length is < 3 or > 32 || request.Password.Length < 10) return Results.BadRequest(new { error = "Enter a valid email, username (3–32 chars), and password (at least 10 chars)." });
    if (await db.Accounts.AnyAsync(x => x.Email == email || x.Username == username)) return Results.Conflict(new { error = "Email or username already registered." });
    var amount = decimal.TryParse(config["Trading:StartingBalance"], out var configured) ? configured : 10000m;
    if (amount <= 0) return Results.Problem("Starting balance configuration is invalid.");
    var account = new TradingAccount { Email = email, Username = username, UsdBalance = amount, StartingCredit = amount };
    account.PasswordHash = hasher.HashPassword(account, request.Password);
    db.Accounts.Add(account); db.Ledger.Add(new WalletLedgerEntry { AccountId = account.Id, Type = LedgerType.InitialCredit, Amount = amount, BalanceAfter = amount, Description = "Initial virtual simulation credit" });
    await db.SaveChangesAsync(); var session = IssueRefreshToken(db, account); await db.SaveChangesAsync(); SetRefreshCookie(context, session.rawToken); return Results.Ok(new { token = CreateToken(account, config), user = PublicUser(account) });
}).RequireRateLimiting("api");
app.MapPost("/api/auth/login", async (LoginRequest req, TradingDbContext db, IPasswordHasher<TradingAccount> hasher, IConfiguration config, HttpContext context) => {
    var account = await db.Accounts.SingleOrDefaultAsync(x => x.Email == req.Email.Trim().ToLowerInvariant());
    if (account is null || account.Status != AccountStatus.Active || hasher.VerifyHashedPassword(account, account.PasswordHash, req.Password) == PasswordVerificationResult.Failed) return Results.Unauthorized();
    account.LastLoginAt = DateTime.UtcNow; var session = IssueRefreshToken(db, account); await db.SaveChangesAsync(); SetRefreshCookie(context, session.rawToken); return Results.Ok(new { token = CreateToken(account, config), user = PublicUser(account) });
}).RequireRateLimiting("api");
app.MapPost("/api/auth/refresh", async (TradingDbContext db, IConfiguration config, HttpContext context, CancellationToken ct) => {
    if (!context.Request.Cookies.TryGetValue("refreshToken", out var raw) || string.IsNullOrWhiteSpace(raw)) return Results.Unauthorized();
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    var hash = HashRefreshToken(raw); var current = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
    if (current is null) return Results.Unauthorized();
    if (current.RevokedAt is not null)
    {
        var family = await db.RefreshTokens.Where(x => x.FamilyId == current.FamilyId && x.RevokedAt == null).ToListAsync(ct);
        foreach (var token in family) token.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); ClearRefreshCookie(context); return Results.Unauthorized();
    }
    if (current.ExpiresAt <= DateTime.UtcNow) { current.RevokedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); ClearRefreshCookie(context); return Results.Unauthorized(); }
    var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == current.AccountId && x.Status == AccountStatus.Active, ct);
    if (account is null) { current.RevokedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); ClearRefreshCookie(context); return Results.Unauthorized(); }
    current.RevokedAt = DateTime.UtcNow;
    var next = RotateRefreshToken(account.Id, current.FamilyId); current.ReplacedById = next.entity.Id; db.RefreshTokens.Add(next.entity);
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); SetRefreshCookie(context, next.rawToken);
    return Results.Ok(new { token = CreateToken(account, config), user = PublicUser(account) });
}).RequireRateLimiting("api");
app.MapPost("/api/auth/logout", async (TradingDbContext db, HttpContext context, CancellationToken ct) => {
    if (context.Request.Cookies.TryGetValue("refreshToken", out var raw) && !string.IsNullOrWhiteSpace(raw))
    {
        var hash = HashRefreshToken(raw); var stored = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (stored is not null && stored.RevokedAt is null) { stored.RevokedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
    }
    ClearRefreshCookie(context); return Results.NoContent();
}).RequireRateLimiting("api");

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/users/me", async (ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); var a = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == id); return Results.Ok(PublicUser(a)); });
api.MapPost("/users/change-password", async (ClaimsPrincipal user, ChangePasswordRequest req, TradingDbContext db, IPasswordHasher<TradingAccount> hasher, CancellationToken ct) => {
    if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 10) return Results.BadRequest(new { error = "New password must be at least 10 characters." });
    var accountId = UserId(user); var account = await db.Accounts.SingleAsync(x => x.Id == accountId, ct);
    if (hasher.VerifyHashedPassword(account, account.PasswordHash, req.CurrentPassword) == PasswordVerificationResult.Failed) return Results.BadRequest(new { error = "Current password is incorrect." });
    if (req.CurrentPassword == req.NewPassword) return Results.BadRequest(new { error = "Choose a different password." });
    account.PasswordHash = hasher.HashPassword(account, req.NewPassword); await db.SaveChangesAsync(ct); return Results.NoContent();
});
api.MapGet("/wallet", async (ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); var a = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == id); var reserved=await db.Orders.AsNoTracking().Where(x=>x.AccountId==id&&x.Status==OrderStatus.Open&&x.Side==OrderSide.Buy).SumAsync(x=>(decimal?)x.ReservedAmount)??0m; return Results.Ok(new { currency = "USD", virtualFunds = true, balance = a.UsdBalance, reservedBalance=reserved,totalUsd=a.UsdBalance+reserved,startingCredit = a.StartingCredit }); });
api.MapGet("/wallet/ledger", async (ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); return Results.Ok(await db.Ledger.AsNoTracking().Where(x => x.AccountId == id).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync()); });
api.MapGet("/orders", async (ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); var rows=await db.Orders.AsNoTracking().Where(x => x.AccountId == id).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(); return Results.Ok(rows.Select(OrderView)); });
api.MapGet("/assets", async (ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); return Results.Ok(await db.Assets.AsNoTracking().Where(x => x.AccountId == id && x.Quantity > 0).ToListAsync()); });
api.MapPost("/orders", async (ClaimsPrincipal user, CreateOrderRequest req, TradingDbContext db, IHttpClientFactory clients, IConfiguration config, CancellationToken ct) => {
    var id = UserId(user); var symbol = req.Symbol.Trim().ToUpperInvariant();
    if (symbol.Length is < 5 or > 20 || !symbol.EndsWith("USDT") || req.Quantity <= 0 || req.Quantity > 1_000_000_000m || string.IsNullOrWhiteSpace(req.IdempotencyKey) || req.IdempotencyKey.Length > 128) return Results.BadRequest(new { error = "Invalid spot order; provide a USDT market, positive quantity, and idempotencyKey." });
    var isLimit = req.Type.Equals("Limit", StringComparison.OrdinalIgnoreCase);
    var isMarket = req.Type.Equals("Market", StringComparison.OrdinalIgnoreCase);
    if ((!isMarket && !isLimit) || (isLimit && (req.LimitPrice is null or <= 0)) || (isMarket && req.LimitPrice is not null) || !(req.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) || req.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase))) return Results.BadRequest(new { error = "Choose Buy/Sell Market or Limit; limit orders require a positive limitPrice." });
    if (req.Leverage is < 1 or > 10) return Results.BadRequest(new { error = "Leverage must be between 1x and 10x." });
    if (req.Leverage > 1 && (!isMarket || req.StopLoss is <= 0 || req.TakeProfit is <= 0)) return Results.BadRequest(new { error = "Leveraged simulation supports market orders only; stop-loss and take-profit must be positive when provided." });
    var existing = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == id && x.IdempotencyKey == req.IdempotencyKey, ct); if (existing is not null) return Results.Ok(OrderView(existing));
    if (req.Leverage > 1) { var existingMargin = await db.MarginPositions.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == id && x.IdempotencyKey == req.IdempotencyKey, ct); if (existingMargin is not null) return Results.Ok(MarginPositionView(existingMargin, existingMargin.ExitPrice ?? existingMargin.EntryPrice)); }
    var response = await clients.CreateClient("binance").GetAsync($"/api/v3/ticker/price?symbol={Uri.EscapeDataString(symbol)}", ct);
    if (!response.IsSuccessStatusCode) return Results.Problem("Live market price is unavailable; no order was placed.", statusCode: 503);
    using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    if (!json.RootElement.TryGetProperty("price", out var priceValue) || !decimal.TryParse(priceValue.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price) || price <= 0) return Results.Problem("Market provider returned invalid price data.", statusCode: 503);
    var feeRate = decimal.TryParse(config["Trading:FeeRate"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsedRate) ? parsedRate : 0.001m;
    if (feeRate is < 0 or > 0.1m) return Results.Problem("Fee configuration is invalid.");
    var baseAsset = symbol[..^4]; var notional = decimal.Round(req.Quantity * price, 8); var fee = TradingRules.Fee(notional, feeRate);
    if (req.Leverage > 1)
    {
        var side = req.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy;
        if (side == OrderSide.Buy && ((req.StopLoss is not null && req.StopLoss >= price) || (req.TakeProfit is not null && req.TakeProfit <= price)) || side == OrderSide.Sell && ((req.StopLoss is not null && req.StopLoss <= price) || (req.TakeProfit is not null && req.TakeProfit >= price))) return Results.BadRequest(new { error = "For long positions stop-loss must be below and take-profit above entry; short positions use the reverse." });
        await using var marginTx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var marginAccount = await db.Accounts.SingleAsync(x => x.Id == id, ct);
        var collateral = decimal.Round(notional / req.Leverage, 8);
        if (marginAccount.Status != AccountStatus.Active) return Results.Forbid();
        if (marginAccount.UsdBalance < collateral + fee) return Results.BadRequest(new { error = "Insufficient virtual USD for collateral and opening fee." });
        marginAccount.UsdBalance -= collateral + fee;
        var liquidation = side == OrderSide.Buy ? price * (1m - 1m / req.Leverage + .05m) : price * (1m + 1m / req.Leverage - .05m);
        var position = new MarginPosition { AccountId = id, Symbol = symbol, IdempotencyKey = req.IdempotencyKey, Side = side, Quantity = req.Quantity, Leverage = req.Leverage, EntryPrice = price, Collateral = collateral, EntryFee = fee, StopLoss = req.StopLoss, TakeProfit = req.TakeProfit, LiquidationPrice = decimal.Round(liquidation, 12) };
        db.MarginPositions.Add(position);
        db.Ledger.Add(new WalletLedgerEntry { AccountId = id, Type = LedgerType.Adjustment, Amount = -collateral, BalanceAfter = marginAccount.UsdBalance + fee, Description = $"Reserved collateral for {req.Leverage}x {side} {symbol}" });
        if (fee > 0) db.Ledger.Add(new WalletLedgerEntry { AccountId = id, Type = LedgerType.Fee, Amount = -fee, BalanceAfter = marginAccount.UsdBalance, Description = "Simulated leveraged opening fee" });
        db.Notifications.Add(new AppNotification { AccountId = id, Type = "MarginPositionOpened", Message = $"Opened {req.Leverage}x {side} {symbol}; liquidation near {liquidation:N4}." });
        await db.SaveChangesAsync(ct); await marginTx.CommitAsync(ct); return Results.Ok(MarginPositionView(position, price, feeRate));
    }
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    var account = await db.Accounts.SingleAsync(x => x.Id == id, ct);
    if (account.Status != AccountStatus.Active) return Results.Forbid();
    var order = new TradingOrder { AccountId = id, Symbol = symbol, BaseAsset = baseAsset, Side = req.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy, Type = isLimit ? OrderType.Limit : OrderType.Market, Status = isLimit ? OrderStatus.Open : OrderStatus.Filled, Quantity = req.Quantity, LimitPrice = req.LimitPrice, FillPrice = isLimit ? null : price, Fee = isLimit ? 0 : fee, IdempotencyKey = req.IdempotencyKey, FilledAt = isLimit ? null : DateTime.UtcNow };
    var balance = await db.Assets.SingleOrDefaultAsync(x => x.AccountId == id && x.Asset == baseAsset, ct);
    if (isLimit)
    {
      if (order.Side == OrderSide.Buy)
      {
        order.ReservedAmount = TradingRules.LimitBuyReserve(req.Quantity, req.LimitPrice!.Value, feeRate);
        if (account.UsdBalance < order.ReservedAmount) return Results.BadRequest(new { error = "Insufficient virtual USD balance to reserve this limit order." });
        account.UsdBalance -= order.ReservedAmount;
        db.Ledger.Add(new WalletLedgerEntry { AccountId=id,Type=LedgerType.OrderReserve,Amount=-order.ReservedAmount,BalanceAfter=account.UsdBalance,Description=$"Reserved USD for limit buy {symbol}",OrderId=order.Id });
      }
      else
      {
        if (balance is null || balance.Quantity - balance.ReservedQuantity < req.Quantity) return Results.BadRequest(new { error = "Insufficient available asset balance to reserve this limit sell." });
        balance.ReservedQuantity += req.Quantity;
        db.Ledger.Add(new WalletLedgerEntry { AccountId=id,Type=LedgerType.OrderReserve,Amount=-req.Quantity,Currency=baseAsset,BalanceAfter=account.UsdBalance,Description=$"Reserved {baseAsset} for limit sell {symbol}",OrderId=order.Id });
      }
      db.Orders.Add(order); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(OrderView(order));
    }
    if (order.Side == OrderSide.Buy) {
      if (account.UsdBalance < notional + fee) return Results.BadRequest(new { error = "Insufficient virtual USD balance." });
      account.UsdBalance -= notional + fee;
      if (balance is null) { balance = new AssetBalance { AccountId = id, Asset = baseAsset }; db.Assets.Add(balance); }
      (balance.Quantity, balance.AverageCost) = TradingRules.AddToPosition(balance.Quantity, balance.AverageCost, req.Quantity, price);
    } else {
      if (balance is null || balance.Quantity - balance.ReservedQuantity < req.Quantity) return Results.BadRequest(new { error = "Insufficient available asset balance." });
      balance.Quantity -= req.Quantity; account.UsdBalance += notional - fee; order.RealizedPnl = TradingRules.RealizedPnl(req.Quantity, balance.AverageCost, price, fee);
    }
    db.Orders.Add(order);
    db.Ledger.Add(new WalletLedgerEntry { AccountId = id, Type = order.Side == OrderSide.Buy ? LedgerType.Buy : LedgerType.Sell, Amount = order.Side == OrderSide.Buy ? -notional : notional, BalanceAfter = account.UsdBalance, Description = $"Simulated {order.Side} {req.Quantity} {baseAsset} at {price}", OrderId = order.Id });
    if (fee > 0) db.Ledger.Add(new WalletLedgerEntry { AccountId = id, Type = LedgerType.Fee, Amount = -fee, BalanceAfter = account.UsdBalance, Description = "Simulated trading fee", OrderId = order.Id });
    db.Notifications.Add(new AppNotification { AccountId=id,Type="OrderFilled",Message=$"Market {order.Side} {symbol} filled at {price:N8}." });
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(OrderView(order));
}).RequireRateLimiting("api");
api.MapGet("/margin-positions", async (ClaimsPrincipal user, TradingDbContext db, IDistributedCache cache, IConfiguration config, CancellationToken ct) => { var id = UserId(user); var feeRate = decimal.TryParse(config["Trading:FeeRate"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsedRate) ? parsedRate : .001m; var rows = await db.MarginPositions.AsNoTracking().Where(x => x.AccountId == id).OrderByDescending(x => x.OpenedAt).Take(200).ToListAsync(ct); var result = new List<object>(); foreach (var row in rows) { var mark = row.ExitPrice ?? row.EntryPrice; if (row.Status == MarginPositionStatus.Open) { var cached = await cache.GetStringAsync($"market:ticker:{row.Symbol}", ct); if (cached is not null) try { mark = System.Text.Json.JsonSerializer.Deserialize<MarketTick>(cached, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Price ?? mark; } catch { } } result.Add(MarginPositionView(row, mark, feeRate)); } return Results.Ok(result); });
api.MapPut("/margin-positions/{id:guid}/risk", async (Guid id, UpdateMarginStopsRequest req, ClaimsPrincipal user, TradingDbContext db, IDistributedCache cache, IHttpClientFactory clients, IConfiguration config, CancellationToken ct) => {
    var accountId = UserId(user); var position = await db.MarginPositions.SingleOrDefaultAsync(x => x.Id == id && x.AccountId == accountId, ct); if (position is null) return Results.NotFound(); if (position.Status != MarginPositionStatus.Open) return Results.Conflict(new { error = "Risk levels can only be changed while the position is open." });
    var mark = position.EntryPrice; var cached = await cache.GetStringAsync($"market:ticker:{position.Symbol}", ct); if (cached is not null) try { mark = System.Text.Json.JsonSerializer.Deserialize<MarketTick>(cached, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Price ?? mark; } catch { }
    if (cached is null) { var response = await clients.CreateClient("binance").GetAsync($"/api/v3/ticker/price?symbol={Uri.EscapeDataString(position.Symbol)}", ct); if (!response.IsSuccessStatusCode) return Results.Problem("Live price is unavailable; risk levels were not changed.", statusCode: 503); using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); if (!decimal.TryParse(doc.RootElement.GetProperty("price").GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out mark) || mark <= 0) return Results.Problem("Market provider returned invalid price data.", statusCode: 503); }
    if (req.StopLoss is <= 0 || req.TakeProfit is <= 0 || (position.Side == OrderSide.Buy && ((req.StopLoss is not null && req.StopLoss >= mark) || (req.TakeProfit is not null && req.TakeProfit <= mark))) || (position.Side == OrderSide.Sell && ((req.StopLoss is not null && req.StopLoss <= mark) || (req.TakeProfit is not null && req.TakeProfit >= mark)))) return Results.BadRequest(new { error = "For a long position stop-loss must be below and take-profit above the current price; short positions use the reverse. Send null to clear a level." });
    position.StopLoss = req.StopLoss; position.TakeProfit = req.TakeProfit; await db.SaveChangesAsync(ct); var feeRate = decimal.TryParse(config["Trading:FeeRate"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsedRate) ? parsedRate : .001m; return Results.Ok(MarginPositionView(position, mark, feeRate));
}).RequireRateLimiting("api");
api.MapPost("/margin-positions/{id:guid}/close", async (Guid id, ClaimsPrincipal user, TradingDbContext db, IHttpClientFactory clients, IConfiguration config, CancellationToken ct) => {
    var accountId = UserId(user); var preview = await db.MarginPositions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.AccountId == accountId, ct); if (preview is null) return Results.NotFound(); if (preview.Status != MarginPositionStatus.Open) return Results.Conflict(new { error = "Position is already closed." });
    var response = await clients.CreateClient("binance").GetAsync($"/api/v3/ticker/price?symbol={Uri.EscapeDataString(preview.Symbol)}", ct); if (!response.IsSuccessStatusCode) return Results.Problem("Live market price is unavailable; position was not closed.", statusCode: 503);
    using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); if (!decimal.TryParse(json.RootElement.GetProperty("price").GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price) || price <= 0) return Results.Problem("Market provider returned invalid price data.", statusCode: 503);
    var feeRate = decimal.TryParse(config["Trading:FeeRate"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsedRate) ? parsedRate : .001m;
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var position = await db.MarginPositions.FromSqlInterpolated($"SELECT * FROM \"MarginPositions\" WHERE \"Id\" = {id} AND \"AccountId\" = {accountId} FOR UPDATE").SingleOrDefaultAsync(ct);
    if (position is null) return Results.NotFound(); if (position.Status != MarginPositionStatus.Open) return Results.Conflict(new { error = "Position is already closed." });
    var account = await db.Accounts.SingleAsync(x => x.Id == accountId, ct);
    var gross = (position.Side == OrderSide.Buy ? price - position.EntryPrice : position.EntryPrice - price) * position.Quantity; var exitFee = TradingRules.Fee(position.Quantity * price, feeRate); var preFeeRelease = Math.Max(0m, position.Collateral + gross); var paidExitFee = Math.Min(preFeeRelease, exitFee); var released = preFeeRelease - paidExitFee;
    position.Status = MarginPositionStatus.Closed; position.ExitPrice = price; position.RealizedPnl = gross - position.EntryFee - paidExitFee; position.ClosedAt = DateTime.UtcNow; account.UsdBalance += released;
    db.Ledger.Add(new WalletLedgerEntry { AccountId = accountId, Type = LedgerType.Adjustment, Amount = preFeeRelease, BalanceAfter = account.UsdBalance + paidExitFee, Description = $"Released collateral and P&L for simulated margin position {position.Symbol}" });
    if (paidExitFee > 0) db.Ledger.Add(new WalletLedgerEntry { AccountId = accountId, Type = LedgerType.Fee, Amount = -paidExitFee, BalanceAfter = account.UsdBalance, Description = "Simulated leveraged closing fee" });
    db.Notifications.Add(new AppNotification { AccountId = accountId, Type = "MarginPositionClosed", Message = $"Closed {position.Symbol} at {price:N4}; realized P&L {position.RealizedPnl:+0.00;-0.00}." }); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(MarginPositionView(position, price, feeRate));
}).RequireRateLimiting("api");
api.MapDelete("/orders/{id:guid}", async (Guid id, ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) => {
    var accountId=UserId(user);await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
    var order=await db.Orders.SingleOrDefaultAsync(x=>x.Id==id&&x.AccountId==accountId,ct);if(order is null)return Results.NotFound();if(order.Status!=OrderStatus.Open)return Results.Conflict(new{error="Only open orders can be cancelled."});
    var account=await db.Accounts.SingleAsync(x=>x.Id==accountId,ct);
    if(order.Side==OrderSide.Buy&&order.ReservedAmount>0){account.UsdBalance+=order.ReservedAmount;db.Ledger.Add(new WalletLedgerEntry{AccountId=accountId,Type=LedgerType.OrderRelease,Amount=order.ReservedAmount,BalanceAfter=account.UsdBalance,Description="Limit buy cancelled; USD reserve released",OrderId=order.Id});order.ReservedAmount=0;}
    else if(order.Side==OrderSide.Sell){var asset=await db.Assets.SingleAsync(x=>x.AccountId==accountId&&x.Asset==order.BaseAsset,ct);asset.ReservedQuantity-=order.Quantity;db.Ledger.Add(new WalletLedgerEntry{AccountId=accountId,Type=LedgerType.OrderRelease,Amount=order.Quantity,Currency=order.BaseAsset,BalanceAfter=account.UsdBalance,Description="Limit sell cancelled; asset reserve released",OrderId=order.Id});}
    order.Status=OrderStatus.Cancelled;db.Notifications.Add(new AppNotification{AccountId=accountId,Type="OrderCancelled",Message=$"Limit {order.Side} {order.Symbol} was cancelled."});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(OrderView(order));
}).RequireRateLimiting("api");
api.MapGet("/trades", async(ClaimsPrincipal user,TradingDbContext db)=>{var id=UserId(user);return Results.Ok(await db.Orders.AsNoTracking().Where(x=>x.AccountId==id&&x.Status==OrderStatus.Filled).OrderByDescending(x=>x.FilledAt).Take(200).Select(x=>new{x.Id,x.Symbol,side=x.Side.ToString(),x.Quantity,x.FillPrice,x.Fee,x.RealizedPnl,x.FilledAt}).ToListAsync());});

api.MapGet("/markets", async (IHttpClientFactory clients, IDistributedCache cache, CancellationToken ct) => { var cached=await cache.GetStringAsync("market:ticker:24hr",ct); if(cached is not null)return Results.Content(cached,"application/json");var res = await clients.CreateClient("binance").GetAsync("/api/v3/ticker/24hr", ct); if (!res.IsSuccessStatusCode) return Results.Problem("Live market data is unavailable.", statusCode: 503); var body=await res.Content.ReadAsStringAsync(ct); await cache.SetStringAsync("market:ticker:24hr",body,new DistributedCacheEntryOptions{AbsoluteExpirationRelativeToNow=TimeSpan.FromSeconds(5)},ct); return Results.Content(body, "application/json"); });
api.MapGet("/markets/{symbol}/ticker",async(string symbol,IHttpClientFactory clients,IDistributedCache cache,CancellationToken ct)=>{symbol=symbol.ToUpperInvariant();if(!symbol.EndsWith("USDT")||symbol.Length>20)return Results.BadRequest();var cached=await cache.GetStringAsync($"market:ticker:{symbol}",ct);if(cached is not null)return Results.Content(cached,"application/json");var response=await clients.CreateClient("binance").GetAsync($"/api/v3/ticker/24hr?symbol={Uri.EscapeDataString(symbol)}",ct);return response.IsSuccessStatusCode?Results.Content(await response.Content.ReadAsStringAsync(ct),"application/json"):Results.Problem("Market ticker is stale or unavailable.",statusCode:503);});
api.MapGet("/markets/{symbol}/candles", async (string symbol, string? interval, int? limit, IHttpClientFactory clients, CancellationToken ct) => {
    var valid = new[] { "1m", "5m", "15m", "30m", "1h", "4h", "1d", "1w" }; var tf = interval ?? "1h";
    if (!valid.Contains(tf) || !symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Unsupported symbol or interval." });
    var response = await clients.CreateClient("binance").GetAsync($"/api/v3/klines?symbol={Uri.EscapeDataString(symbol.ToUpperInvariant())}&interval={tf}&limit={Math.Clamp(limit ?? 300, 1, 1000)}", ct);
    return response.IsSuccessStatusCode ? Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json") : Results.Problem("Live candle data is unavailable.", statusCode: 503);
});
api.MapGet("/favorites", async (ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); return Results.Ok(await db.Favorites.AsNoTracking().Where(x => x.AccountId == id).OrderBy(x => x.Symbol).Select(x => x.Symbol).ToListAsync()); });
api.MapPost("/favorites/{symbol}", async (string symbol, ClaimsPrincipal user, TradingDbContext db) => { symbol = symbol.ToUpperInvariant(); if (!symbol.EndsWith("USDT") || symbol.Length > 20) return Results.BadRequest(); var id = UserId(user); if (!await db.Favorites.AnyAsync(x => x.AccountId == id && x.Symbol == symbol)) db.Favorites.Add(new Favorite { AccountId = id, Symbol = symbol }); await db.SaveChangesAsync(); return Results.Ok(); });
api.MapDelete("/favorites/{symbol}", async (string symbol, ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); symbol=symbol.ToUpperInvariant(); var f = await db.Favorites.SingleOrDefaultAsync(x => x.AccountId == id && x.Symbol == symbol); if (f is null) return Results.NoContent(); db.Remove(f); await db.SaveChangesAsync(); return Results.NoContent(); });
api.MapGet("/alerts", async(ClaimsPrincipal user,TradingDbContext db)=>{var id=UserId(user);return Results.Ok(await db.PriceAlerts.AsNoTracking().Where(x=>x.AccountId==id).OrderByDescending(x=>x.CreatedAt).ToListAsync());});
api.MapPost("/alerts", async(ClaimsPrincipal user,CreateAlertRequest req,TradingDbContext db)=>{var symbol=req.Symbol.Trim().ToUpperInvariant();if(!symbol.EndsWith("USDT")||symbol.Length>20||req.TargetPrice<=0||!Enum.TryParse<AlertCondition>(req.Condition,true,out var condition))return Results.BadRequest(new{error="Provide a valid USDT symbol, positive target price, and Above or Below condition."});var row=new PriceAlert{AccountId=UserId(user),Symbol=symbol,TargetPrice=req.TargetPrice,Condition=condition};db.PriceAlerts.Add(row);await db.SaveChangesAsync();return Results.Ok(row);});
api.MapDelete("/alerts/{id:guid}",async(Guid id,ClaimsPrincipal user,TradingDbContext db)=>{var accountId=UserId(user);var alert=await db.PriceAlerts.SingleOrDefaultAsync(x=>x.Id==id&&x.AccountId==accountId);if(alert is null)return Results.NoContent();db.PriceAlerts.Remove(alert);await db.SaveChangesAsync();return Results.NoContent();});
api.MapGet("/portfolio", async (ClaimsPrincipal user, TradingDbContext db, IHttpClientFactory clients, IDistributedCache cache, IConfiguration config, CancellationToken ct) => { var id=UserId(user); var feeRate=decimal.TryParse(config["Trading:FeeRate"],System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out var parsedRate)?parsedRate:.001m; var account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==id); var assets=await db.Assets.AsNoTracking().Where(x=>x.AccountId==id && x.Quantity>0).ToListAsync(); var values=new List<object>(); decimal total=account.UsdBalance; foreach(var a in assets){var symbol=a.Asset+"USDT"; try { var resp=await clients.CreateClient("binance").GetAsync($"/api/v3/ticker/price?symbol={symbol}",ct); using var doc=System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)); var p=decimal.Parse(doc.RootElement.GetProperty("price").GetString()!,System.Globalization.CultureInfo.InvariantCulture); var value=a.Quantity*p;total+=value;values.Add(new {a.Asset,a.Quantity,a.AverageCost,price=p,value,unrealizedPnl=(p-a.AverageCost)*a.Quantity}); } catch { values.Add(new {a.Asset,a.Quantity,a.AverageCost,price=(decimal?)null,value=(decimal?)null,unrealizedPnl=(decimal?)null}); }} var positions=await db.MarginPositions.AsNoTracking().Where(x=>x.AccountId==id&&x.Status==MarginPositionStatus.Open).ToListAsync(ct); var positionValues=new List<object>(); foreach(var p in positions){var mark=p.EntryPrice;var cached=await cache.GetStringAsync($"market:ticker:{p.Symbol}",ct);if(cached is not null)try{mark=System.Text.Json.JsonSerializer.Deserialize<MarketTick>(cached,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Price??mark;}catch{}var gross=(p.Side==OrderSide.Buy?mark-p.EntryPrice:p.EntryPrice-mark)*p.Quantity;var estimatedExitFee=TradingRules.Fee(p.Quantity*mark,feeRate);total+=p.Collateral+gross-estimatedExitFee;positionValues.Add(MarginPositionView(p,mark,feeRate));} return Results.Ok(new {simulation=true,usdBalance=account.UsdBalance,totalValue=total,assets=values,marginPositions=positionValues}); });

api.MapPost("/wallet/credit-requests", async (ClaimsPrincipal user, CreditRequestInput req, TradingDbContext db) => {
    if (req.Amount <= 0 || req.Amount > 1_000_000m || string.IsNullOrWhiteSpace(req.Description) || req.Description.Length > 500) return Results.BadRequest(new { error="Enter an amount from 0.01 to 1,000,000 and a short explanation." });
    var id=UserId(user); var row=new VirtualCreditRequest { AccountId=id, Amount=decimal.Round(req.Amount,8), Description=req.Description.Trim(), RequestNumber=$"VC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..24] };
    db.CreditRequests.Add(row); await db.SaveChangesAsync(); return Results.Ok(CreditRequestView(row));
});
api.MapGet("/wallet/credit-requests", async (ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); var rows=await db.CreditRequests.AsNoTracking().Where(x=>x.AccountId==id).OrderByDescending(x=>x.CreatedAt).Take(100).ToListAsync(); return Results.Ok(rows.Select(CreditRequestView)); });
api.MapGet("/notifications", async (ClaimsPrincipal user, TradingDbContext db) => { var id=UserId(user); return Results.Ok(await db.Notifications.AsNoTracking().Where(x=>x.AccountId==id).OrderByDescending(x=>x.CreatedAt).Take(100).Select(x=>new {x.Id,x.Type,x.Message,x.IsRead,x.CreatedAt}).ToListAsync()); });
api.MapPost("/notifications/{id:guid}/read", async (Guid id, ClaimsPrincipal user, TradingDbContext db) => { var accountId=UserId(user);var n=await db.Notifications.SingleOrDefaultAsync(x=>x.Id==id && x.AccountId==accountId); if(n is null)return Results.NotFound();n.IsRead=true;await db.SaveChangesAsync();return Results.NoContent(); });

var admin=app.MapGroup("/api/admin").RequireAuthorization(p=>p.RequireRole("Admin"));
admin.MapGet("/dashboard", async (TradingDbContext db) => Results.Ok(new { simulation=true,totalUsers=await db.Accounts.CountAsync(),activeUsers=await db.Accounts.CountAsync(x=>x.Status==AccountStatus.Active),suspendedUsers=await db.Accounts.CountAsync(x=>x.Status==AccountStatus.Suspended),todayRegistrations=await db.Accounts.CountAsync(x=>x.CreatedAt>=DateTime.UtcNow.Date),pendingCreditRequests=await db.CreditRequests.CountAsync(x=>x.Status==CreditRequestStatus.Pending),simulatedTradesToday=await db.Orders.CountAsync(x=>x.CreatedAt>=DateTime.UtcNow.Date),totalVirtualCredit=await db.Ledger.Where(x=>x.Type==LedgerType.InitialCredit||x.Type==LedgerType.AdminCredit).SumAsync(x=>(decimal?)x.Amount)??0m }));
admin.MapGet("/users", async (string? search, int? page, TradingDbContext db) => { var q=db.Accounts.AsNoTracking().AsQueryable(); if(!string.IsNullOrWhiteSpace(search)){var s=search.Trim().ToLowerInvariant();q=q.Where(x=>x.Email.ToLower().Contains(s)||x.Username.ToLower().Contains(s));} return Results.Ok(await q.OrderBy(x=>x.CreatedAt).Skip((Math.Clamp(page??1,1,10000)-1)*50).Take(50).Select(x=>new{x.Id,x.Username,x.Email,role=x.Role,status=x.Status.ToString(),x.UsdBalance,x.CreatedAt}).ToListAsync()); });
admin.MapGet("/credit-requests", async (string? status, TradingDbContext db) => { var q=db.CreditRequests.AsNoTracking().AsQueryable(); if(Enum.TryParse<CreditRequestStatus>(status,true,out var state))q=q.Where(x=>x.Status==state); return Results.Ok(await q.OrderByDescending(x=>x.CreatedAt).Take(200).Select(x=>new{x.Id,x.RequestNumber,x.AccountId,x.Amount,x.Description,status=x.Status.ToString(),x.AdminResponse,x.CreatedAt}).ToListAsync()); });
admin.MapPost("/credit-requests/{id:guid}/approve", async (Guid id, ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) => {
    await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var request=await db.CreditRequests.SingleOrDefaultAsync(x=>x.Id==id,ct);if(request is null)return Results.NotFound();if(request.Status!=CreditRequestStatus.Pending)return Results.Conflict(new{error="This request has already been reviewed."});var account=await db.Accounts.SingleAsync(x=>x.Id==request.AccountId,ct);var before=account.UsdBalance;account.UsdBalance+=request.Amount;request.Status=CreditRequestStatus.Approved;request.ReviewedByAdminId=UserId(user);request.ReviewedAt=DateTime.UtcNow;db.Ledger.Add(new WalletLedgerEntry{AccountId=account.Id,Type=LedgerType.AdminCredit,Amount=request.Amount,BalanceAfter=account.UsdBalance,Description=$"Approved virtual credit request {request.RequestNumber}"});db.AdminAuditLogs.Add(new AdminAuditLog{AdminAccountId=UserId(user),AffectedAccountId=account.Id,Action="CreditRequestApproved",PreviousBalance=before,NewBalance=account.UsdBalance,Amount=request.Amount,Reason=request.Description,RelatedCreditRequestId=request.Id});db.Notifications.Add(new AppNotification{AccountId=account.Id,Type="VirtualCreditApproved",Message=$"Your virtual credit request {request.RequestNumber} was approved: ${request.Amount:N2}."});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(CreditRequestView(request));
});
admin.MapPost("/credit-requests/{id:guid}/reject", async (Guid id, ReviewRequestInput req, ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) => { if(string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>500)return Results.BadRequest(new{error="A rejection reason is required (max 500 characters)."});await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var r=await db.CreditRequests.SingleOrDefaultAsync(x=>x.Id==id,ct);if(r is null)return Results.NotFound();if(r.Status!=CreditRequestStatus.Pending)return Results.Conflict(new{error="This request has already been reviewed."});r.Status=CreditRequestStatus.Rejected;r.AdminResponse=req.Reason.Trim();r.ReviewedByAdminId=UserId(user);r.ReviewedAt=DateTime.UtcNow;db.AdminAuditLogs.Add(new AdminAuditLog{AdminAccountId=UserId(user),AffectedAccountId=r.AccountId,Action="CreditRequestRejected",Amount=r.Amount,Reason=req.Reason.Trim(),RelatedCreditRequestId=r.Id});db.Notifications.Add(new AppNotification{AccountId=r.AccountId,Type="VirtualCreditRejected",Message=$"Your virtual credit request {r.RequestNumber} was rejected: {req.Reason.Trim()}"});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(CreditRequestView(r));});
admin.MapPost("/users/{id:guid}/virtual-credit", async (Guid id, AdminCreditInput req, ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) => { if(req.Amount<=0||req.Amount>1_000_000m||string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>500)return Results.BadRequest(new{error="Enter a positive amount under 1,000,000 and a reason."});await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var a=await db.Accounts.SingleOrDefaultAsync(x=>x.Id==id,ct);if(a is null)return Results.NotFound();var before=a.UsdBalance;a.UsdBalance+=decimal.Round(req.Amount,8);db.Ledger.Add(new WalletLedgerEntry{AccountId=id,Type=LedgerType.AdminCredit,Amount=decimal.Round(req.Amount,8),BalanceAfter=a.UsdBalance,Description=req.Reason.Trim()});db.AdminAuditLogs.Add(new AdminAuditLog{AdminAccountId=UserId(user),AffectedAccountId=id,Action="AdminCredit",PreviousBalance=before,NewBalance=a.UsdBalance,Amount=decimal.Round(req.Amount,8),Reason=req.Reason.Trim()});db.Notifications.Add(new AppNotification{AccountId=id,Type="AdminCredit",Message=$"An administrator added ${req.Amount:N2} in virtual funds. Reason: {req.Reason.Trim()}"});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Results.Ok(new{a.Id,balance=a.UsdBalance});});
admin.MapPost("/users/{id:guid}/suspend", async (Guid id, AdminReasonInput req, ClaimsPrincipal user, TradingDbContext db) => {if(string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>500)return Results.BadRequest(new{error="A reason is required."});var a=await db.Accounts.SingleOrDefaultAsync(x=>x.Id==id);if(a is null)return Results.NotFound();a.Status=AccountStatus.Suspended;db.AdminAuditLogs.Add(new AdminAuditLog{AdminAccountId=UserId(user),AffectedAccountId=id,Action="UserSuspended",Reason=req.Reason.Trim()});await db.SaveChangesAsync();return Results.Ok(new{a.Id,status=a.Status.ToString()});});
admin.MapPost("/users/{id:guid}/activate", async (Guid id, AdminReasonInput req, ClaimsPrincipal user, TradingDbContext db) => {if(string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>500)return Results.BadRequest(new{error="A reason is required."});var a=await db.Accounts.SingleOrDefaultAsync(x=>x.Id==id);if(a is null)return Results.NotFound();a.Status=AccountStatus.Active;db.AdminAuditLogs.Add(new AdminAuditLog{AdminAccountId=UserId(user),AffectedAccountId=id,Action="UserActivated",Reason=req.Reason.Trim()});await db.SaveChangesAsync();return Results.Ok(new{a.Id,status=a.Status.ToString()});});
admin.MapGet("/audit-logs", async (int? page, TradingDbContext db) => Results.Ok(await db.AdminAuditLogs.AsNoTracking().OrderByDescending(x=>x.CreatedAt).Skip((Math.Clamp(page??1,1,10000)-1)*50).Take(50).Select(x=>new{x.Id,x.AdminAccountId,x.AffectedAccountId,x.Action,x.PreviousBalance,x.NewBalance,x.Amount,x.Reason,x.RelatedCreditRequestId,x.CreatedAt}).ToListAsync()));

app.Run();
static Guid UserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
static (RefreshToken entity,string rawToken) IssueRefreshToken(TradingDbContext db,TradingAccount account){var raw=Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));var token=new RefreshToken{AccountId=account.Id,TokenHash=HashRefreshToken(raw),ExpiresAt=DateTime.UtcNow.AddDays(30)};db.RefreshTokens.Add(token);return(token,raw);}
static RefreshToken NewRefreshToken(Guid accountId,Guid familyId,string rawToken)=>new(){AccountId=accountId,FamilyId=familyId,TokenHash=HashRefreshToken(rawToken),ExpiresAt=DateTime.UtcNow.AddDays(30)};
static (RefreshToken entity,string rawToken) RotateRefreshToken(Guid accountId,Guid familyId){var raw=Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));return(NewRefreshToken(accountId,familyId,raw),raw);}
static string HashRefreshToken(string token)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)));
static void SetRefreshCookie(HttpContext context,string value)=>context.Response.Cookies.Append("refreshToken",value,new CookieOptions{HttpOnly=true,Secure=context.Request.IsHttps,SameSite=SameSiteMode.Strict,Path="/api/auth",Expires=DateTimeOffset.UtcNow.AddDays(30),IsEssential=true});
static void ClearRefreshCookie(HttpContext context)=>context.Response.Cookies.Delete("refreshToken",new CookieOptions{HttpOnly=true,Secure=context.Request.IsHttps,SameSite=SameSiteMode.Strict,Path="/api/auth"});
static object PublicUser(TradingAccount a) => new { id=a.Id,email=a.Email,username=a.Username,role=a.Role,status=a.Status.ToString(),createdAt=a.CreatedAt };
static object OrderView(TradingOrder o) => new { o.Id,o.Symbol,side=o.Side.ToString(),type=o.Type.ToString(),status=o.Status.ToString(),o.Quantity,o.LimitPrice,o.FillPrice,o.Fee,o.RealizedPnl,o.CreatedAt,o.FilledAt };
static object MarginPositionView(MarginPosition p, decimal markPrice, decimal feeRate = .001m) { var gross=(p.Side==OrderSide.Buy?markPrice-p.EntryPrice:p.EntryPrice-markPrice)*p.Quantity; var estimatedExitFee=p.Status==MarginPositionStatus.Open?TradingRules.Fee(p.Quantity*markPrice,feeRate):0m; return new { p.Id,p.Symbol,side=p.Side.ToString(),p.Quantity,p.Leverage,p.EntryPrice,markPrice,notional=p.Quantity*markPrice,p.Collateral,p.EntryFee,estimatedExitFee,totalFeesIfClosed=p.EntryFee+estimatedExitFee,netUnrealizedPnl=gross-p.EntryFee-estimatedExitFee,p.StopLoss,p.TakeProfit,p.LiquidationPrice,status=p.Status.ToString(),unrealizedPnl=gross,p.ExitPrice,p.RealizedPnl,p.OpenedAt,p.ClosedAt }; }
static object CreditRequestView(VirtualCreditRequest r) => new {r.Id,r.RequestNumber,r.Amount,r.Description,status=r.Status.ToString(),r.AdminResponse,r.CreatedAt,r.ReviewedAt};
static string CreateToken(TradingAccount a,IConfiguration c) { var key=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(c["Jwt:SigningKey"]!)); var claims=new[]{new Claim(ClaimTypes.NameIdentifier,a.Id.ToString()),new Claim(ClaimTypes.Name,a.Username),new Claim(ClaimTypes.Role,a.Role)}; return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(c["Jwt:Issuer"]??"TradingEngine",c["Jwt:Audience"]??"TradingEngine.Web",claims,expires:DateTime.UtcNow.AddMinutes(20),signingCredentials:new SigningCredentials(key,SecurityAlgorithms.HmacSha256))); }
record RegisterRequest(string Email,string Username,string Password);
record LoginRequest(string Email,string Password);
record CreateOrderRequest(string Symbol,string Side,string Type,decimal Quantity,string IdempotencyKey,decimal? LimitPrice=null,int Leverage=1,decimal? StopLoss=null,decimal? TakeProfit=null);
record ChangePasswordRequest(string CurrentPassword,string NewPassword);
record UpdateMarginStopsRequest(decimal? StopLoss,decimal? TakeProfit);
record CreditRequestInput(decimal Amount,string Description);
record AdminCreditInput(decimal Amount,string Reason);
record ReviewRequestInput(string Reason);
record AdminReasonInput(string Reason);
record CreateAlertRequest(string Symbol,string Condition,decimal TargetPrice);
