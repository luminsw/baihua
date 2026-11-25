using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Baihua.Data;

/// <summary>
/// FamilyDbContext 设计时工厂（dotnet ef 命令使用）。
/// </summary>
public class FamilyDbContextFactory : IDesignTimeDbContextFactory<FamilyDbContext>
{
    public FamilyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FamilyDbContext>();
        DbConnections.Configure(options);
        return new FamilyDbContext(options.Options);
    }
}
