namespace TradingEngine.Domain;

public enum AccountStatus { Active, Suspended, Closed }
public enum OrderSide { Buy, Sell }
public enum OrderType { Market, Limit }
public enum OrderStatus { Open, Filled, Cancelled, Rejected }
public enum LedgerType { InitialCredit, Buy, Sell, Fee, OrderReserve, OrderRelease, AdminCredit, AdminDebit, Adjustment }
public enum CreditRequestStatus { Pending, Approved, Rejected, Cancelled }
public enum AlertCondition { Above, Below }
public enum MarginPositionStatus { Open, Closed, Liquidated, StoppedOut, TakeProfit }

public sealed class TradingAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "User";
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public decimal UsdBalance { get; set; }
    public decimal StartingCredit { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public List<AssetBalance> Assets { get; set; } = [];
    public List<TradingOrder> Orders { get; set; } = [];
    public List<WalletLedgerEntry> Ledger { get; set; } = [];
}

public sealed class MarginPosition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Symbol { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public OrderSide Side { get; set; }
    public decimal Quantity { get; set; }
    public int Leverage { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal Collateral { get; set; }
    public decimal EntryFee { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal LiquidationPrice { get; set; }
    public MarginPositionStatus Status { get; set; } = MarginPositionStatus.Open;
    public decimal? ExitPrice { get; set; }
    public decimal? RealizedPnl { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}

public sealed class AssetBalance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public TradingAccount? Account { get; set; }
    public string Asset { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal ReservedQuantity { get; set; }
    public decimal AverageCost { get; set; }
}

public sealed class TradingOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public TradingAccount? Account { get; set; }
    public string Symbol { get; set; } = "";
    public string BaseAsset { get; set; } = "";
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public OrderStatus Status { get; set; }
    public decimal Quantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? FillPrice { get; set; }
    public decimal Fee { get; set; }
    public decimal ReservedAmount { get; set; }
    public decimal RealizedPnl { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FilledAt { get; set; }
}

public sealed class WalletLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public TradingAccount? Account { get; set; }
    public LedgerType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Currency { get; set; } = "USD";
    public string Description { get; set; } = "";
    public Guid? OrderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Favorite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Symbol { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class VirtualCreditRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RequestNumber { get; set; } = "";
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    public CreditRequestStatus Status { get; set; } = CreditRequestStatus.Pending;
    public string? AdminResponse { get; set; }
    public Guid? ReviewedByAdminId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
}

public sealed class AdminAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AdminAccountId { get; set; }
    public Guid AffectedAccountId { get; set; }
    public string Action { get; set; } = "";
    public decimal? PreviousBalance { get; set; }
    public decimal? NewBalance { get; set; }
    public decimal? Amount { get; set; }
    public string Reason { get; set; } = "";
    public Guid? RelatedCreditRequestId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AppNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid FamilyId { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

public sealed class PriceAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Symbol { get; set; } = "";
    public AlertCondition Condition { get; set; }
    public decimal TargetPrice { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? TriggeredAt { get; set; }
}
