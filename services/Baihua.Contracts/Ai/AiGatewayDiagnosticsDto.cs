namespace Baihua.Contracts.Ai;

public sealed class AiGatewayDiagnosticsDto
{
    public List<AiProviderDiagnosticsDto> Providers { get; set; } = new();
}

public sealed class AiProviderDiagnosticsDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AiModelTier Tier { get; set; }
    public bool? IsReachable { get; set; }
    public string? ErrorCode { get; set; }
    public List<AiModelDiagnosticsDto> Models { get; set; } = new();
}

public sealed class AiModelDiagnosticsDto
{
    public string Id { get; set; } = "";
    public bool IsConfigured { get; set; }
    public bool? IsAvailable { get; set; }
}
