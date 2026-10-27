using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure;

namespace TradingEngine.IntegrationTests;

public class TradingModelTests
{
    [Fact]
    public void FinancialModel_UsesDecimalPrecisionAndAccountScopedIdempotency()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new TradingDbContext(options);
        var order = db.Model.FindEntityType(typeof(TradingEngine.Domain.TradingOrder))!;
        var idempotency = order.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "AccountId", "IdempotencyKey" }));
        Assert.True(idempotency.IsUnique);
        Assert.Equal("numeric(28,12)", order.FindProperty(nameof(TradingEngine.Domain.TradingOrder.Quantity))!.GetColumnType());
        var account = db.Model.FindEntityType(typeof(TradingEngine.Domain.TradingAccount))!;
        Assert.Equal("numeric(28,8)", account.FindProperty(nameof(TradingEngine.Domain.TradingAccount.UsdBalance))!.GetColumnType());
        var refresh = db.Model.FindEntityType(typeof(TradingEngine.Domain.RefreshToken))!;
        Assert.True(refresh.GetIndexes().Any(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(TradingEngine.Domain.RefreshToken.TokenHash) }) && i.IsUnique));
        var alert = db.Model.FindEntityType(typeof(TradingEngine.Domain.PriceAlert))!;
        Assert.Equal("numeric(28,12)", alert.FindProperty(nameof(TradingEngine.Domain.PriceAlert.TargetPrice))!.GetColumnType());
    }
}
