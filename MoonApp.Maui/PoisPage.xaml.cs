using System.Collections.ObjectModel;
using MoonApp.Core;

namespace MoonApp.Maui;

/// <summary>
/// Hledání dominant v okolí (OpenStreetMap + výškopis). Obdoba záložky „Body v okolí“ na webu;
/// výběr položky vrátí souřadnice a hlavní stránka je nastaví jako objekt focení.
/// </summary>
public partial class PoisPage : ContentPage
{
    public sealed class Row
    {
        public Poi Poi { get; init; } = null!;
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public string SkyText { get; init; } = "";
        public Color SkyColor { get; init; } = Colors.Gray;
        public string DistText { get; init; } = "";
    }

    readonly double _lat, _lon;
    readonly string _cacheDir;
    readonly ObservableCollection<Row> _rows = [];
    readonly HashSet<string> _off = [];        // vypnuté kategorie
    List<Poi> _items = [];
    CancellationTokenSource? _cts;
    TaskCompletionSource<Poi?> _tcs = new();

    /// <summary>Nejmenší převýšení nad krajinou, aby mělo smysl to fotit (web: 15 m).</summary>
    const double SkyMin = 15;

    public PoisPage(double centerLat, double centerLon, string cacheDir)
    {
        InitializeComponent();
        _lat = centerLat; _lon = centerLon; _cacheDir = cacheDir;
        List.ItemsSource = _rows;
        RadiusP.ItemsSource = new[] { "5 km", "10 km", "20 km" };
        RadiusP.SelectedIndex = 1;
        BuildCategoryChips();
    }

    public Task<Poi?> PickAsync() => _tcs.Task;

    void BuildCategoryChips()
    {
        CatBox.Children.Clear();
        foreach (var (slug, label) in Pois.Categories)
        {
            var b = new Button
            {
                Text = label,
                FontSize = 12,
                Padding = new Thickness(12, 0),
                HeightRequest = 40,          // dosahová plocha, ne jen text
                CornerRadius = 20,
                Margin = new Thickness(0, 0, 6, 6),
                BackgroundColor = Colors.Transparent,
                BorderWidth = 1,
                ClassId = slug,
            };
            b.Clicked += (_, _) =>
            {
                if (!_off.Remove(slug)) _off.Add(slug);
                StyleChip(b, slug);
                Render();
            };
            StyleChip(b, slug);
            CatBox.Children.Add(b);
        }
    }

    void StyleChip(Button b, string slug)
    {
        bool on = !_off.Contains(slug);
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        b.TextColor = on ? Color.FromArgb(dark ? "#4F8EF7" : "#2563EB")
                         : Color.FromArgb(dark ? "#8A94A6" : "#64748B");
        b.BorderColor = on ? Color.FromArgb(dark ? "#4F8EF7" : "#2563EB")
                           : Color.FromArgb(dark ? "#2A3550" : "#D6DEEA");
    }

    async void OnSearch(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        double r = RadiusP.SelectedIndex switch { 0 => 5000, 2 => 20000, _ => 10000 };

        SearchBtn.IsEnabled = false;
        Busy.IsRunning = Busy.IsVisible = true;
        var progress = new Progress<ProgressInfo>(p => NoteLbl.Text = p.Stage);
        try
        {
            _items = await Task.Run(() => Pois.FindAsync(_lat, _lon, r, cacheDir: _cacheDir,
                progress: progress, ct: ct), ct);
            Render();
            // výškopis je pomalý — nejdřív se ukáže seznam, měření dobíhá a průběžně se dokresluje
            var top = _items.Take(36).ToList();
            await Task.Run(() => Pois.ScoreAsync(top, _cacheDir, progress, ct), ct);
            if (!ct.IsCancellationRequested) Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { NoteLbl.Text = "Nepodařilo se prohledat okolí: " + ex.Message; }
        finally
        {
            Busy.IsRunning = Busy.IsVisible = false;
            SearchBtn.IsEnabled = true;
        }
    }

    void Render()
    {
        var vis = _items.Where(p => !_off.Contains(p.Cat)).ToList();
        _rows.Clear();
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        foreach (var p in vis.Take(200))
        {
            string label = Pois.Categories.First(c => c.Slug == p.Cat).Label;
            _rows.Add(new Row
            {
                Poi = p,
                Title = p.Name ?? label,
                Sub = p.Name is null ? "" : label,
                SkyText = p.Sky is { } s ? $"{s:0} m" : "—",
                SkyColor = p.Sky is { } sk && sk >= SkyMin
                    ? Color.FromArgb(dark ? "#34D399" : "#16A34A")
                    : Color.FromArgb(dark ? "#8A94A6" : "#64748B"),
                DistText = $"{p.DistM / 1000:0.0} km",
            });
        }
        int measured = vis.Count(p => p.Sky is not null);
        NoteLbl.Text = vis.Count == 0
            ? "nic nenalezeno — zkus větší okruh nebo zapni víc kategorií"
            : $"{vis.Count} kandidátů · změřeno {measured} · zeleně to, co čouhá aspoň {SkyMin:0} m nad krajinu";
    }

    void OnPickItem(object? sender, TappedEventArgs e)
    {
        if ((sender as Element)?.BindingContext is Row row) Finish(row.Poi);
    }

    void OnClose(object? sender, EventArgs e) => Finish(null);

    void Finish(Poi? pick)
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
