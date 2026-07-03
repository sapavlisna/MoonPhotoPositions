using System.Globalization;

namespace MoonApp.Maui;

public partial class SettingsPage : ContentPage
{
    readonly PlannerSettings _s;

    public SettingsPage(PlannerSettings s)
    {
        InitializeComponent();
        _s = s;
        DateP.Date = s.Date;
        RadiusE.Text = Fmt(s.RadiusM);
        RadMinE.Text = Fmt(s.RadiusMinM);
        ResE.Text = Fmt(s.ResM);
        EyeE.Text = Fmt(s.EyeH);
        SubjE.Text = Fmt(s.SubjectMinH);
        AzTolE.Text = Fmt(s.AzTol);
        AltBandE.Text = Fmt(s.AltBand);
        VersionLbl.Text = $"MoonApp {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";
    }

    async void OnCheckUpdate(object? sender, EventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        try { await UpdateService.CheckAsync(this, manual: true); }
        finally { UpdateBtn.IsEnabled = true; }
    }

    async void OnSave(object? sender, EventArgs e)
    {
        _s.Date = DateP.Date is { } d ? d : _s.Date;
        _s.RadiusM = Math.Clamp(Parse(RadiusE.Text, _s.RadiusM), 300, 8000);
        _s.RadiusMinM = Math.Clamp(Parse(RadMinE.Text, _s.RadiusMinM), 0, _s.RadiusM);
        _s.ResM = Math.Clamp(Parse(ResE.Text, _s.ResM), 20, 500);
        _s.EyeH = Math.Clamp(Parse(EyeE.Text, _s.EyeH), 0, 500);
        _s.SubjectMinH = Math.Clamp(Parse(SubjE.Text, _s.SubjectMinH), 0, 1000);
        _s.AzTol = Math.Clamp(Parse(AzTolE.Text, _s.AzTol), 0.2, 10);
        _s.AltBand = Math.Clamp(Parse(AltBandE.Text, _s.AltBand), 0.2, 15);
        await Navigation.PopAsync();
    }

    static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);

    static double Parse(string? t, double def)
    {
        if (string.IsNullOrWhiteSpace(t)) return def;
        t = t.Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
    }
}
