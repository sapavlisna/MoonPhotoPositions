using System.Collections.ObjectModel;
using System.Globalization;
using MoonApp.Core;

namespace MoonApp.Maui;

/// <summary>
/// „Kdy odsud vidět“ — dny v rozmezí, kdy těleso projde nad vrcholem objektu. Obdoba stejné
/// záložky na webu, ale počítá se pro jedno stanoviště: mřížka přes celé okolí × všechny dny
/// by na telefonu běžela hodiny.
/// </summary>
public partial class WindowPage : ContentPage
{
    public sealed class Row
    {
        public DateOnly Date { get; init; }
        public string DateText { get; init; } = "";
        public string TimeText { get; init; } = "";
        public string AltText { get; init; } = "";
    }

    readonly double _objLat, _objLon, _objTop, _obsLat, _obsLon;
    readonly Body _body;
    readonly PlannerSettings _settings;
    readonly string _cacheDir;
    readonly ObservableCollection<Row> _rows = [];
    CancellationTokenSource? _cts;
    readonly TaskCompletionSource<DateOnly?> _tcs = new();

    public WindowPage(double objLat, double objLon, double objTop, double obsLat, double obsLon,
        Body body, PlannerSettings settings, string cacheDir)
    {
        InitializeComponent();
        _objLat = objLat; _objLon = objLon; _objTop = objTop;
        _obsLat = obsLat; _obsLon = obsLon;
        _body = body; _settings = settings; _cacheDir = cacheDir;
        List.ItemsSource = _rows;

        var today = DateTime.Today;
        FromP.Date = today;
        ToP.Date = today.AddDays(89);
        IntroLbl.Text = $"Projde dny v rozmezí a vypíše ty, kdy {Names.Nom(_body)} projde nad vrcholem " +
                        "objektu. Klepnutí na den ho nastaví v plánovači.";
    }

    /// <summary>Vybraný den (nastaví se v plánovači), nebo null při zavření.</summary>
    public Task<DateOnly?> PickAsync() => _tcs.Task;

    async void OnCompute(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var from = DateOnly.FromDateTime(FromP.Date ?? DateTime.Today);
        var to = DateOnly.FromDateTime(ToP.Date ?? DateTime.Today.AddDays(89));
        if (to < from) { NoteLbl.Text = "Konec rozmezí je před začátkem."; return; }
        // strop drží výpočet v jednotkách sekund i na slabším telefonu
        if (to.DayNumber - from.DayNumber > 400)
        {
            to = from.AddDays(400);
            ToP.Date = to.ToDateTime(TimeOnly.MinValue);
            NoteLbl.Text = "Rozmezí zkráceno na 400 dní.";
        }

        GoBtn.IsEnabled = false;
        Busy.IsRunning = Busy.IsVisible = true;
        _rows.Clear();
        var progress = new Progress<ProgressInfo>(p => NoteLbl.Text = p.Stage);
        try
        {
            var days = await Task.Run(() => SkyWindow.ForPointAsync(
                _objLat, _objLon, _objTop, _obsLat, _obsLon, from, to, _body,
                _settings.EyeH, _settings.AzTol, _settings.AltBand,
                cacheDir: _cacheDir, progress: progress, ct: ct), ct);
            if (ct.IsCancellationRequested) return;

            var cz = CultureInfo.GetCultureInfo("cs-CZ");
            foreach (var d in days)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(d.BestUtc, Time.Prague);
                _rows.Add(new Row
                {
                    Date = d.Date,
                    DateText = d.Date.ToDateTime(TimeOnly.MinValue).ToString("ddd d.M.yyyy", cz),
                    TimeText = local.ToString("HH:mm"),
                    AltText = $"{d.Alt:0.0}° · ±{d.AzErrDeg:0.0}°",
                });
            }
            NoteLbl.Text = days.Count == 0
                ? $"V rozmezí {Names.Nom(_body)} nad objektem neprojde — zkus delší rozmezí nebo jiné stanoviště."
                : $"{days.Count} {(days.Count == 1 ? "den" : days.Count < 5 ? "dny" : "dní")} · nejbližší {_rows[0].DateText} v {_rows[0].TimeText}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { NoteLbl.Text = "Nepodařilo se spočítat: " + ex.Message; }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
            GoBtn.IsEnabled = true;
        }
    }

    void OnPickDay(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is Row row) Finish(row.Date);
    }

    void OnClose(object? sender, EventArgs e) => Finish(null);

    void Finish(DateOnly? pick)
    {
        _cts?.Cancel();
        _tcs.TrySetResult(pick);
        _ = Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        Finish(null);
        return true;
    }
}
