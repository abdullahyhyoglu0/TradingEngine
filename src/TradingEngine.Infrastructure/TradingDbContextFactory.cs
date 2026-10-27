using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradingEngine.Infrastructure;

public sealed class TradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql("Host=localhost;Database=tradingengine;Username=postgres;Password=postgres").Options);
}
