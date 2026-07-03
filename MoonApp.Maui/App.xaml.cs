using Microsoft.Extensions.DependencyInjection;

namespace MoonApp.Maui;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		UserAppTheme = ThemeFromPref(Preferences.Default.Get("theme", "system"));
	}

	public static AppTheme ThemeFromPref(string t) => t switch
	{
		"light" => AppTheme.Light,
		"dark" => AppTheme.Dark,
		_ => AppTheme.Unspecified,   // sledovat systém
	};

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}