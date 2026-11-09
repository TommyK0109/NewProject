using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CSharp2.Infrastructure;

public sealed class StockFlowDbContextFactory : IDesignTimeDbContextFactory<StockFlowDbContext>
{
    public StockFlowDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__StockFlow")
            ?? "Server=localhost;Database=newDB;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";
        return new StockFlowDbContext(new DbContextOptionsBuilder<StockFlowDbContext>().UseSqlServer(connection).Options);
    }
}
