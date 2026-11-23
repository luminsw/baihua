namespace MobileApp.Maui.Services;

/// <summary>
/// 二维码扫描器抽象。返回扫描到的文本内容；取消或失败返回 null。
/// </summary>
public interface IQrScanner
{
    Task<string?> ScanAsync();
}
