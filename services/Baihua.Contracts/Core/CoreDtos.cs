namespace Baihua.Contracts.Core;

public class UpdateServerAddressRequest
{
    public string? Domain { get; set; }
    public string? DisplayName { get; set; }
}

public class ServerAddressResponse
{
    public string? Domain { get; set; }
    public string? Url { get; set; }
    public string? ActualUrl { get; set; }
    public string? HostName { get; set; }
    public string? DisplayName { get; set; }
}