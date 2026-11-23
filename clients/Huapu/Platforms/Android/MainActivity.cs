using Android.App;
using Android.Content.PM;
using Android.OS;

namespace MobileApp.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    LaunchMode = LaunchMode.SingleTask)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Workaround: 始终不传 savedInstanceState，避免 MAUI NavigationRootManager
        // 在 Activity 重建时找不到对应视图的崩溃（.NET 9/10 在部分设备上的已知问题）。
        base.OnCreate(null);
    }
}
