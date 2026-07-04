using Android.App;
using Android.Content.PM;
using Android.OS;

namespace MoonApp.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // Tmavou stavovou lištu (#111827) řeší colorPrimary v Platforms/Android/Resources/values/colors.xml.
    // Nezasahujeme do SystemUiVisibility — rozbíjelo to edge-to-edge insety (posun dotyků vs. vykreslení).
}
