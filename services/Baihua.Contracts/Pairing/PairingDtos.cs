namespace Baihua.Contracts.Pairing;

public class ServerQRResponse
{
    public string Url { get; set; } = "";
    public string HostName { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string QrCodeData { get; set; } = "";
}

public class AiKeyQRResponse
{
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public class RegisterDeviceRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string? DeviceType { get; set; }

    /// <summary>
    /// 系统设备名（如"HUAWEI P60"），与花记名（DeviceName）互补存储
    /// </summary>
    public string? SystemDeviceName { get; set; }
}