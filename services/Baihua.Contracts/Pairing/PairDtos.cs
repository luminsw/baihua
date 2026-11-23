namespace Baihua.Contracts.Pairing;

public class PairRequest
{
    public string? PairCode { get; set; }
    public string? DeviceName { get; set; }
}

public class PairResponse
{
    public string? RequestId { get; set; }
    public string? AccessToken { get; set; }
    public long ExpiresIn { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}