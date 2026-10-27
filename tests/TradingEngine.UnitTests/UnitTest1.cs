using TradingEngine.Application;
using TradingEngine.Domain;

namespace TradingEngine.UnitTests;

public class TradingRulesTests
{
    [Fact]
    public void Fee_UsesConfiguredRateAndRoundsToEightDecimals() => Assert.Equal(1.23456789m, TradingRules.Fee(1234.56789m, 0.001m));

    [Fact]
    public void AddingToPosition_UsesWeightedAverageCost()
    {
        var (quantity, average) = TradingRules.AddToPosition(2m, 100m, 3m, 120m);
        Assert.Equal(5m, quantity);
        Assert.Equal(112m, average);
    }

    [Fact]
    public void RealizedPnl_DeductsFee() => Assert.Equal(18m, TradingRules.RealizedPnl(2m, 100m, 110m, 2m));

    [Fact]
    public void AddingInvalidPosition_IsRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => TradingRules.AddToPosition(0m, 0m, 0m, 1m));

    [Fact]
    public void LimitBuy_ReservesNotionalAndFee() => Assert.Equal(100.10m, TradingRules.LimitBuyReserve(1m, 100m, .001m));

    [Theory]
    [InlineData(OrderSide.Buy, 99, 100, true)]
    [InlineData(OrderSide.Buy, 101, 100, false)]
    [InlineData(OrderSide.Sell, 101, 100, true)]
    [InlineData(OrderSide.Sell, 99, 100, false)]
    public void LimitOrder_TriggersAtTheExpectedSideOfTheLimit(OrderSide side, decimal market, decimal limit, bool expected) =>
        Assert.Equal(expected, TradingRules.ShouldExecuteLimit(side, market, limit));
}
