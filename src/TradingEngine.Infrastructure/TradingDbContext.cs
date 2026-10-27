using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;

namespace TradingEngine.Infrastructure;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<TradingAccount> Accounts => Set<TradingAccount>();
    public DbSet<AssetBalance> Assets => Set<AssetBalance>();
    public DbSet<TradingOrder> Orders => Set<TradingOrder>();
    public DbSet<MarginPosition> MarginPositions => Set<MarginPosition>();
    public DbSet<WalletLedgerEntry> Ledger => Set<WalletLedgerEntry>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<VirtualCreditRequest> CreditRequests => Set<VirtualCreditRequest>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();
    public DbSet<AppNotification> Notifications => Set<AppNotification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TradingAccount>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.Email).IsUnique(); e.HasIndex(x => x.Username).IsUnique(); e.Property(x => x.Email).HasMaxLength(320); e.Property(x => x.Username).HasMaxLength(32); e.Property(x => x.UsdBalance).HasPrecision(28, 8); e.Property(x => x.StartingCredit).HasPrecision(28, 8); });
        b.Entity<AssetBalance>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AccountId, x.Asset }).IsUnique(); e.Property(x => x.Quantity).HasPrecision(28, 12); e.Property(x => x.ReservedQuantity).HasPrecision(28, 12); e.Property(x => x.AverageCost).HasPrecision(28, 12); e.HasOne(x => x.Account).WithMany(x => x.Assets).HasForeignKey(x => x.AccountId); });
        b.Entity<TradingOrder>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique(); e.HasIndex(x => new { x.Status, x.Symbol }); e.Property(x => x.Quantity).HasPrecision(28, 12); e.Property(x => x.LimitPrice).HasPrecision(28, 12); e.Property(x => x.FillPrice).HasPrecision(28, 12); e.Property(x => x.Fee).HasPrecision(28, 8); e.Property(x => x.ReservedAmount).HasPrecision(28, 8); e.Property(x => x.RealizedPnl).HasPrecision(28, 8); e.Property(x => x.Symbol).HasMaxLength(32); e.Property(x => x.BaseAsset).HasMaxLength(16); e.Property(x => x.IdempotencyKey).HasMaxLength(128); e.HasOne(x => x.Account).WithMany(x => x.Orders).HasForeignKey(x => x.AccountId); });
        b.Entity<MarginPosition>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AccountId, x.Status, x.OpenedAt }); e.HasIndex(x => new { x.Symbol, x.Status }); e.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique(); e.Property(x => x.Symbol).HasMaxLength(32); e.Property(x => x.IdempotencyKey).HasMaxLength(128); e.Property(x => x.Quantity).HasPrecision(28, 12); e.Property(x => x.EntryPrice).HasPrecision(28, 12); e.Property(x => x.Collateral).HasPrecision(28, 8); e.Property(x => x.EntryFee).HasPrecision(28, 8); e.Property(x => x.StopLoss).HasPrecision(28, 12); e.Property(x => x.TakeProfit).HasPrecision(28, 12); e.Property(x => x.LiquidationPrice).HasPrecision(28, 12); e.Property(x => x.ExitPrice).HasPrecision(28, 12); e.Property(x => x.RealizedPnl).HasPrecision(28, 8); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<WalletLedgerEntry>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AccountId, x.CreatedAt }); e.Property(x => x.Amount).HasPrecision(28, 8); e.Property(x => x.BalanceAfter).HasPrecision(28, 8); e.Property(x => x.Currency).HasMaxLength(12); e.Property(x => x.Description).HasMaxLength(500); e.HasOne(x => x.Account).WithMany(x => x.Ledger).HasForeignKey(x => x.AccountId); });
        b.Entity<Favorite>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AccountId, x.Symbol }).IsUnique(); e.Property(x => x.Symbol).HasMaxLength(32); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<VirtualCreditRequest>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.RequestNumber).IsUnique(); e.HasIndex(x => new { x.AccountId, x.CreatedAt }); e.Property(x => x.RequestNumber).HasMaxLength(24); e.Property(x => x.Amount).HasPrecision(28, 8); e.Property(x => x.Description).HasMaxLength(500); e.Property(x => x.AdminResponse).HasMaxLength(500); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<AdminAuditLog>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AffectedAccountId, x.CreatedAt }); e.Property(x => x.Action).HasMaxLength(80); e.Property(x => x.PreviousBalance).HasPrecision(28, 8); e.Property(x => x.NewBalance).HasPrecision(28, 8); e.Property(x => x.Amount).HasPrecision(28, 8); e.Property(x => x.Reason).HasMaxLength(500); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AdminAccountId).OnDelete(DeleteBehavior.Restrict); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AffectedAccountId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<AppNotification>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AccountId, x.CreatedAt }); e.Property(x => x.Type).HasMaxLength(80); e.Property(x => x.Message).HasMaxLength(500); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<RefreshToken>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => new { x.AccountId, x.FamilyId }); e.Property(x => x.TokenHash).HasMaxLength(64); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<PriceAlert>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.Symbol, x.IsEnabled, x.TriggeredAt }); e.HasIndex(x => new { x.AccountId, x.CreatedAt }); e.Property(x => x.Symbol).HasMaxLength(32); e.Property(x => x.TargetPrice).HasPrecision(28, 12); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade); });
    }
}
