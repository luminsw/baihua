using BaihuaSdk.Push;
using BaihuaSdk.Services;
using BaihuaSdk.Signing;
using BaihuaSdk.Storage;
using MobileApp.Maui.Services;
using MobileContract.Services;
using ZXing.Net.Maui.Controls;

namespace MobileApp.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureMauiHandlers(handlers =>
            {
                handlers.AddHandler<
                    Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebView,
                    Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebViewHandler>();
            })
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // 后台初始化设备 ID，避免在 CreateMauiApp 期间阻塞 UI 线程
        _ = Task.Run(DeviceInfoHelper.InitializeAsync);

        // Platform services
        builder.Services.AddSingleton<ISecureStore, MauiSecureStore>();
        builder.Services.AddSingleton<IServerConfigStore, MauiServerConfigStore>();
        builder.Services.AddSingleton<IVaultStorageFactory>(sp =>
            new VaultStorageFactory(Path.Combine(FileSystem.AppDataDirectory, "vaults")));
        builder.Services.AddSingleton<IQrScanner, MauiQrScanner>();

        // SDK services
        builder.Services.AddSingleton(sp =>
        {
            var deviceId = DeviceInfoHelper.GetDeviceId();
            var deviceName = DeviceInfoHelper.GetDeviceName();
            return new RequestSigner(deviceId, deviceName,
                websiteBaseUrl: "https://www.shzhengji.com",
                mobileClientSecret: GetMobileClientSecret());
        });
        builder.Services.AddSingleton<IRequestSigner>(sp => sp.GetRequiredService<RequestSigner>());

        builder.Services.AddSingleton(sp =>
        {
            var handler = new HttpClientHandler();
#if DEBUG
            // Debug 构建跳过证书验证，方便本地开发 / 自签名证书场景
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
#endif
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            return client;
        });

        // SDK service implementations (factory registration because constructors need string params)
        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var signer = sp.GetRequiredService<IRequestSigner>();
            return new PairingServiceImpl(client, signer,
                DeviceInfoHelper.GetDeviceId(), DeviceInfoHelper.GetDisplayName(), DeviceInfoHelper.GetDeviceName());
        });
        builder.Services.AddSingleton<IPairingService>(sp => sp.GetRequiredService<PairingServiceImpl>());
        builder.Services.AddSingleton<IDeviceRegistrationService>(sp => sp.GetRequiredService<PairingServiceImpl>());
        builder.Services.AddSingleton<SyncServiceImpl>();
        builder.Services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<SyncServiceImpl>());
        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var pushService = new PushWebSocketService(client)
            {
                // 定向推送：服务端按 deviceId 推送授权/撤销事件，避免多设备互相收到广播
                DeviceId = DeviceInfoHelper.GetDeviceId()
            };
            return pushService;
        });
        builder.Services.AddTransient<AuthorizationWatcher>(sp =>
        {
            var registration = sp.GetRequiredService<IDeviceRegistrationService>();
            var pushService = sp.GetRequiredService<PushWebSocketService>();
            return new AuthorizationWatcher(registration, pushService);
        });
        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var signer = sp.GetRequiredService<IRequestSigner>();
            return new PushPollingServiceImpl(client, signer);
        });
        builder.Services.AddSingleton<IPushPollingService>(sp => sp.GetRequiredService<PushPollingServiceImpl>());

        // Master-apprentice service
        builder.Services.AddSingleton<IMasterCacheService, MasterCacheService>();
        builder.Services.AddSingleton<IMasterService>(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var signer = sp.GetRequiredService<IRequestSigner>();
            var serverStore = sp.GetRequiredService<IServerConfigStore>();
            var cacheService = sp.GetRequiredService<IMasterCacheService>();
            return new MasterService(serverStore, signer, client, cacheService);
        });

        return builder.Build();
    }

    private static string? GetMobileClientSecret()
    {
        var secret = Environment.GetEnvironmentVariable("MOBILE_CLIENT_SECRET");
        return string.IsNullOrEmpty(secret) ? null : secret;
    }
}
