namespace Baihua.Contracts.Health;

public interface IBaihuaHealthApi
{
    Task<SystemHealthReportDto> GetFullHealthAsync(CancellationToken cancellationToken = default);
}

