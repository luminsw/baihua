namespace MobileApp.Maui.Services;

/// <summary>
/// MAUI 原生二维码扫描器实现。弹出一个全屏扫描页，识别到二维码后返回内容。
/// </summary>
public class MauiQrScanner : IQrScanner
{
    public async Task<string?> ScanAsync()
    {
        var page = new Pages.ScanPage();
        var mainPage = GetMainPage();

        await mainPage.Navigation.PushModalAsync(page);
        var result = await page.WaitForResultAsync();

        // 如果页面还在（用户没点取消），确保关闭
        if (mainPage.Navigation.ModalStack.Contains(page))
            await mainPage.Navigation.PopModalAsync();

        return result;
    }

    private static Page GetMainPage()
    {
        var app = Application.Current
            ?? throw new InvalidOperationException("Application.Current is not available.");

        var window = app.Windows.FirstOrDefault()
            ?? throw new InvalidOperationException("No active window found.");

        return window.Page
            ?? throw new InvalidOperationException("Window.Page is not set.");
    }
}
