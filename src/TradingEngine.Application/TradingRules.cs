using TradingEngine.Domain;

namespace TradingEngine.Application;

public static class TradingRules
{
    public static decimal Fee(decimal notional, decimal rate) => decimal.Round(notional * rate, 8, MidpointRounding.ToEven);

    public static (decimal quantity, decimal averageCost) AddToPosition(decimal oldQuantity, decimal oldAverageCost, decimal addedQuantity, decimal price)
    {
        if (addedQuantity <= 0 || price <= 0) throw new ArgumentOutOfRangeException(nameof(addedQuantity));
        var total = oldQuantity + addedQuantity;
        return (total, (oldQuantity * oldAverageCost + addedQuantity * price) / total);
    }

    public static decimal RealizedPnl(decimal soldQuantity, decimal averageCost, decimal salePrice, decimal fee) =>
        soldQuantity * (salePrice - averageCost) - fee;

    public static decimal LimitBuyReserve(decimal quantity, decimal limitPrice, decimal feeRate)
    {
        if (quantity <= 0 || limitPrice <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var notional = decimal.Round(quantity * limitPrice, 8, MidpointRounding.ToEven);
        return notional + Fee(notional, feeRate);
    }

    public static bool ShouldExecuteLimit(OrderSide side, decimal marketPrice, decimal limitPrice) =>
        side == OrderSide.Buy ? marketPrice <= limitPrice : marketPrice >= limitPrice;
}
