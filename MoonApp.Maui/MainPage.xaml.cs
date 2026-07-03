using BruTile.Cache;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Maui;
using MoonApp.Core;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace MoonApp.Maui;

public partial class MainPage : ContentPage
{
    MapControl _map = null!;
    MemoryLayer? _covLayer, _objLayer, _obsLayer, _lineLayer, _meLayer;
    bool _locStarted;
    ILayer[] _bases = null!;
    static readonly string[] BaseNames = ["Mapa", "Satelit", "Ortofoto"];
    int _baseIdx;

    readonly PlannerSettings _settings = new();
    bool _hasObj;
    double _objLat, _objLon, _objTopZ;
    double _obsLat, _obsLon;        // poslední stanoviště
    ViewpointResult? _vp;
    double _vpBearing;

    // stav náhledu (horizont + dráha Měsíce)
    double _winA0, _winA1;          // azimutové okno [°]
    List<MoonSample> _track = [];   // dráha Měsíce (přepočítatelná dle náhledového data)
    int _prevDayOffset;             // posun data v náhledu [dny]
    int _timeIdx;                   // index vzorku v _track
    bool _chartBig;                 // zvětšený náhled
    double _pinchA0, _pinchA1;      // okno na začátku pinch gesta
    double _panA0, _panA1;          // okno na začátku pan gesta
    IDispatcherTimer? _playTimer;

    const double ChartHNormal = 190, ChartHBig = 460;

    // perzistentní cache výškopisu (přežije čištění systémové cache → offline po stažení)
    static string DsmCache => Path.Combine(FileSystem.AppDataDirectory, "dsm");

    public MainPage()
    {
        InitializeComponent();
        BuildMap();
        InitChart();
        DateSel.Date = _settings.Date;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (DateSel.Date != _settings.Date) DateSel.Date = _settings.Date;
        _ = StartLocationAsync();
    }

    async Task StartLocationAsync()
    {
        if (_locStarted) return;
        var st = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (st != PermissionStatus.Granted) st = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (st != PermissionStatus.Granted) return;
        _locStarted = true;
        Geolocation.Default.LocationChanged += (_, e) => UpdateMe(e.Location);
        // rychlé první zobrazení
        try
        {
            var loc = await Geolocation.Default.GetLastKnownLocationAsync()
                ?? await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(8)));
            if (loc != null) UpdateMe(loc);
        }
        catch { /* poloha může chybět */ }
        // živé sledování
        try
        {
            if (!Geolocation.Default.IsListeningForeground)
                await Geolocation.Default.StartListeningForegroundAsync(
                    new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(3)));
        }
        catch { /* sledování nedostupné → zůstane jednorázová poloha */ }
    }

    void UpdateMe(Location loc)
    {
        var (x, y) = SphericalMercator.FromLonLat(loc.Longitude, loc.Latitude);
        Remove(ref _meLayer);
        _meLayer = new MemoryLayer("me")
        {
            Features = [new PointFeature(new MPoint(x, y))],
            Style = new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(66, 133, 244)),
                Outline = new Pen(new Mapsui.Styles.Color(255, 255, 255), 3),
                SymbolScale = 0.55,
            },
        };
        _map.Map.Layers.Add(_meLayer);   // vždy navrch
        _map.Refresh();
    }

    void InitChart()
    {
        Chart.PaintSurface += OnPaintHorizon;
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnChartPan;
        Chart.GestureRecognizers.Add(pan);
        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += OnChartPinch;
        Chart.GestureRecognizers.Add(pinch);
    }

    void BuildMap()
    {
        _map = new MapControl();
        var map = new Mapsui.Map();
        string tiles = Path.Combine(FileSystem.CacheDirectory, "tiles");
        _bases =
        [
            OpenStreetMap.CreateTileLayer(),
            new TileLayer(new HttpTileSource(new GlobalSphericalMercator(),
                "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
                name: "Esri", persistentCache: new FileCache(Path.Combine(tiles, "esri"), "jpg"))) { Name = "Satelit" },
            new TileLayer(new HttpTileSource(new GlobalSphericalMercator(),
                "https://ags.cuzk.cz/arcgis1/rest/services/ORTOFOTO_WM/MapServer/tile/{z}/{y}/{x}",
                name: "CUZK", persistentCache: new FileCache(Path.Combine(tiles, "cuzk"), "jpg"))) { Name = "Ortofoto" },
        ];
        map.Layers.Add(_bases[0]);
        LayerBtn.Text = BaseNames[1];
        var (mx, my) = SphericalMercator.FromLonLat(16.63903, 49.04183);
        var center = new MPoint(mx, my);
        _map.Map = map;
        _map.Loaded += (_, _) => map.Navigator.CenterOnAndZoomTo(center, 19.1);
        Root.Children.Insert(0, _map);
    }

    static (double lat, double lon) Center(MapControl m)
    {
        var vp = m.Map.Navigator.Viewport;
        var (lon, lat) = SphericalMercator.ToLonLat(vp.CenterX, vp.CenterY);
        return (lat, lon);
    }

    async void OnPick(object? sender, EventArgs e)
    {
        var (lat, lon) = Center(_map);
        await PickObject(lat, lon);
    }

    async void OnViewpoint(object? sender, EventArgs e)
    {
        if (!_hasObj) return;
        var (lat, lon) = Center(_map);
        await PickViewpoint(lat, lon);
    }

    void OnToggleLayer(object? sender, EventArgs e)
    {
        _map.Map.Layers.Remove(_bases[_baseIdx]);
        _baseIdx = (_baseIdx + 1) % _bases.Length;
        _map.Map.Layers.Insert(0, _bases[_baseIdx]);       // pod overlaye
        LayerBtn.Text = BaseNames[(_baseIdx + 1) % _bases.Length];
        _map.Refresh();
    }

    async void OnSettings(object? sender, EventArgs e)
        => await Navigation.PushAsync(new SettingsPage(_settings));

    void OnDateSelected(object? sender, DateChangedEventArgs e)
    {
        _settings.Date = DateSel.Date ?? _settings.Date;
        InfoLabel.Text = $"Datum {_settings.Date:d.M.yyyy}. Přepočítej ① Objekt / ② Stanoviště pro nový den.";
    }

    void OnZoomIn(object? sender, EventArgs e)
    {
        var n = _map.Map.Navigator;
        n.ZoomTo(n.Viewport.Resolution / 2, 200);
    }

    void OnZoomOut(object? sender, EventArgs e)
    {
        var n = _map.Map.Navigator;
        n.ZoomTo(n.Viewport.Resolution * 2, 200);
    }

    async Task PickObject(double lat, double lon)
    {
        SetBusy(true, "Připravuji výpočet objektu…");
        var progress = new Progress<ProgressInfo>(OnProgress);
        try
        {
            string cache = DsmCache;
            var snap = await Task.Run(() => Raycast.SnapPeakAsync(lat, lon, 50, 1.0, cache, progress));
            _objLat = snap.Lat; _objLon = snap.Lon; _objTopZ = snap.Top; _hasObj = true;
            var g = await Task.Run(() => Coverage.ComputeAsync(snap.Lat, snap.Lon,
                DateOnly.FromDateTime(_settings.Date), 1, _settings.RadiusM, _settings.ResM, _settings.EyeH,
                Math.Max(_settings.SubjectMinH, snap.Height), _settings.AzTol, _settings.AltBand,
                dMin: Math.Max(80, _settings.RadiusMinM), cacheDir: cache, progress: progress));
            DrawCoverage(g, snap);
            ViewBtn.IsEnabled = true;
            _vp = null; StopPlay(); ChartBorder.IsVisible = false;
            InfoLabel.Text =
                $"OBJEKT {snap.Lat:0.#####}, {snap.Lon:0.#####} · vrchol {snap.Top:0.0} m (výška {snap.Height:0.0} m)\n" +
                $"pokrytí {g.Visible} buněk (max {g.Max}). Teď posuň na STANOVIŠTĚ a klikni.";
        }
        catch (Exception ex) { InfoLabel.Text = "Chyba: " + ex.Message; }
        finally { SetBusy(false); }
    }

    async Task PickViewpoint(double lat, double lon)
    {
        SetBusy(true, "Připravuji analýzu stanoviště…");
        var progress = new Progress<ProgressInfo>(OnProgress);
        try
        {
            string cache = DsmCache;
            var r = await Task.Run(() => Planner.ViewpointAsync(_objLat, _objLon, _objTopZ, lat, lon,
                DateOnly.FromDateTime(_settings.Date), _settings.EyeH, cacheDir: cache, progress: progress));
            _vp = r; _vpBearing = r.Bearing; _obsLat = lat; _obsLon = lon;
            DrawViewpoint(lat, lon);
            ChartBorder.IsVisible = true;
            ChartBody.IsVisible = true; CollapseBtn.Text = "▾";
            SetupChartWindow(r);
            string tip = r.OnTipUtc is { } t
                ? TimeZoneInfo.ConvertTimeFromUtc(t, Time.Prague).ToString("HH:mm")
                : "—";
            InfoLabel.Text =
                $"STANOVIŠTĚ {lat:0.#####}, {lon:0.#####} · {r.DistanceM:0} m, směr {r.Bearing:0}°\n" +
                (r.Clear ? "✅ objekt je vidět" : "⛔ objekt zakrytý terénem") +
                $" · potřebná výška Měsíce {r.ElTargetDeg:0.0}°\n" +
                $"🎯 Měsíc na špici cca v {tip}";
        }
        catch (Exception ex) { InfoLabel.Text = "Chyba: " + ex.Message; }
        finally { SetBusy(false); }
    }

    void SetBusy(bool busy, string? msg = null)
    {
        Busy.IsRunning = busy;
        Busy.IsVisible = busy;
        if (!busy) { Bar.IsVisible = false; Bar.Progress = 0; }
        PickBtn.IsEnabled = !busy;
        ViewBtn.IsEnabled = !busy && _hasObj;
        if (msg != null) InfoLabel.Text = msg;
    }

    // Hlášení průběhu z Core (marshalováno na UI thread přes Progress<T>).
    void OnProgress(ProgressInfo p)
    {
        if (!Busy.IsRunning) return;   // ignoruj opožděné reporty po dokončení
        if (p.Fraction is { } f)
        {
            Bar.IsVisible = true;
            Bar.Progress = Math.Clamp(f, 0, 1);
            InfoLabel.Text = $"{p.Stage}  {f * 100:0} %";
        }
        else
        {
            Bar.IsVisible = false;
            InfoLabel.Text = p.Stage;
        }
    }

    // ---------- mapa: overlaye ----------
    void DrawCoverage(CoverageGrid g, SnapResult snap)
    {
        var map = _map.Map;
        Remove(ref _covLayer); Remove(ref _objLayer);
        byte[] png = HeatmapPng(g);
        var (minX, minY) = SphericalMercator.FromLonLat(g.W, g.S);
        var (maxX, maxY) = SphericalMercator.FromLonLat(g.E, g.N);
        _covLayer = new MemoryLayer("coverage")
        {
            Features = [new RasterFeature(new MRaster(png, new MRect(minX, minY, maxX, maxY)))],
            Style = new RasterStyle(),
        };
        map.Layers.Add(_covLayer);
        _objLayer = Dot(snap.Lon, snap.Lat, new Mapsui.Styles.Color(230, 40, 40), "object");
        map.Layers.Add(_objLayer);
        BringMeToFront();
        _map.Refresh();
    }

    void DrawViewpoint(double lat, double lon)
    {
        var map = _map.Map;
        Remove(ref _obsLayer); Remove(ref _lineLayer);
        var (ox, oy) = SphericalMercator.FromLonLat(lon, lat);
        var (jx, jy) = SphericalMercator.FromLonLat(_objLon, _objLat);
        _lineLayer = new MemoryLayer("line")
        {
            Features = [new GeometryFeature {
                Geometry = new NetTopologySuite.Geometries.LineString(
                    [new(ox, oy), new(jx, jy)]) }],
            Style = new VectorStyle { Line = new Pen(new Mapsui.Styles.Color(60, 130, 255), 3) },
        };
        map.Layers.Add(_lineLayer);
        _obsLayer = Dot(lon, lat, new Mapsui.Styles.Color(60, 130, 255), "obs");
        map.Layers.Add(_obsLayer);
        BringMeToFront();
        _map.Refresh();
    }

    static MemoryLayer Dot(double lon, double lat, Mapsui.Styles.Color c, string name)
    {
        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        return new MemoryLayer(name)
        {
            Features = [new PointFeature(new MPoint(x, y))],
            Style = new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                Fill = new Mapsui.Styles.Brush(c),
                Outline = new Pen(new Mapsui.Styles.Color(255, 255, 255), 2),
                SymbolScale = 0.6,
            },
        };
    }

    void Remove(ref MemoryLayer? layer)
    {
        if (layer != null) { _map.Map.Layers.Remove(layer); layer = null; }
    }

    void BringMeToFront()
    {
        if (_meLayer is null) return;
        _map.Map.Layers.Remove(_meLayer);
        _map.Map.Layers.Add(_meLayer);
    }

    static byte[] HeatmapPng(CoverageGrid g)
    {
        using var bmp = new SKBitmap(g.NCols, g.NRows, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int r = 0; r < g.NRows; r++)
            for (int c = 0; c < g.NCols; c++)
            {
                int s = g.Scores[r * g.NCols + c];
                bmp.SetPixel(c, r, s <= 0 ? new SKColor(0, 0, 0, 0)
                    : Ramp(g.Max > 1 ? (double)(s - 1) / (g.Max - 1) : 1.0, 170));
            }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static SKColor Ramp(double t, byte a)
    {
        t = Math.Clamp(t, 0, 1);
        return new SKColor((byte)(255 + (70 - 255) * t), (byte)(196 + (255 - 196) * t),
            (byte)(0 + (110 - 0) * t), a);
    }

    // ---------- interaktivní náhled: čas, zoom, pan, datum ----------
    void SetupChartWindow(ViewpointResult vp)
    {
        _track = [.. vp.Track];
        _prevDayOffset = 0;
        DateSlider.Value = 0;
        FitWindow();
        _timeIdx = DefaultIdx();
        TimeSlider.Maximum = Math.Max(0, _track.Count - 1);
        TimeSlider.Value = _timeIdx;   // → OnTimeChanged
        UpdateDateLabel();
        UpdateMoon();
    }

    int DefaultIdx()
    {
        if (_vp is { } vp && _prevDayOffset == 0 && vp.OnTipUtc is { } tip)
        {
            int t = IndexOfTime(_track, tip);
            if (t >= 0) return t;
        }
        for (int i = 0; i < _track.Count; i++)
            if (_track[i].Alt > HorizonAt(_horizonOf(), _track[i].Az)) return i;
        return _track.Count / 2;
    }

    (double Az, double El)[] _horizonOf() => _vp is { } vp ? vp.Horizon : [];

    static int IndexOfTime(IReadOnlyList<MoonSample> track, DateTime utc)
    {
        for (int i = 0; i < track.Count; i++) if (track[i].TimeUtc == utc) return i;
        return -1;
    }

    void FitWindow()
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var s in _track) if (s.Alt > -2) { if (s.Az < lo) lo = s.Az; if (s.Az > hi) hi = s.Az; }
        lo = Math.Min(lo, _vpBearing); hi = Math.Max(hi, _vpBearing);
        if (lo > hi || hi - lo > 200) { _winA0 = 0; _winA1 = 360; return; }
        double pad = Math.Max(10, (hi - lo) * 0.15); lo -= pad; hi += pad;
        if (hi - lo < 50) { double c = (lo + hi) / 2; lo = c - 25; hi = c + 25; }
        _winA0 = lo; _winA1 = hi;
    }

    void ChartZoom(double f)
    {
        double pivot = _track.Count > 0 ? _track[_timeIdx].Az : (_winA0 + _winA1) / 2;
        double half = Math.Max((_winA1 - _winA0) * f / 2, 4);
        _winA0 = pivot - half; _winA1 = pivot + half;
        Chart.InvalidateSurface();
    }

    void UpdateMoon()
    {
        if (_vp is not { } vp || _track.Count == 0) return;
        var s = _track[_timeIdx];
        double he = HorizonAt(vp.Horizon, s.Az);
        string state = s.Alt <= 0 ? "pod obzorem" : s.Alt > he ? "nad obzorem (vidět)" : "za překážkou";
        var local = TimeZoneInfo.ConvertTimeFromUtc(s.TimeUtc, Time.Prague);
        TimeInfo.Text = $"🕒 {local:HH:mm} — az {s.Az:0.0}°, alt {s.Alt:0.0}° · {state}";
        Chart.InvalidateSurface();
    }

    void OnTimeChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_track.Count == 0) return;
        _timeIdx = Math.Clamp((int)Math.Round(e.NewValue), 0, _track.Count - 1);
        UpdateMoon();
    }

    // Datum posuvníkem — přepočítá JEN dráhu Měsíce (astro, bez sítě). Terén/pokrytí se nemění.
    void OnPreviewDateChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_vp is null) return;
        int off = (int)Math.Round(e.NewValue);
        if (off == _prevDayOffset) return;
        _prevDayOffset = off;
        var date = _settings.Date.AddDays(off);
        var localStart = new DateTime(date.Year, date.Month, date.Day, 16, 0, 0, DateTimeKind.Unspecified);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, Time.Prague);
        _track = Astro.Track(_obsLat, _obsLon, utcStart, utcStart.AddHours(16), 2);
        TimeSlider.Maximum = Math.Max(0, _track.Count - 1);
        _timeIdx = Math.Clamp(_timeIdx, 0, Math.Max(0, _track.Count - 1));
        UpdateDateLabel();
        UpdateMoon();
    }

    void UpdateDateLabel()
    {
        var date = _settings.Date.AddDays(_prevDayOffset);
        string rel = _prevDayOffset == 0 ? "dnes vybráno" : _prevDayOffset > 0 ? $"+{_prevDayOffset} dní" : $"{_prevDayOffset} dní";
        DateLbl.Text = $"📅 {date:d.M.} ({rel})";
    }

    void OnPlay(object? sender, EventArgs e)
    {
        if (_playTimer != null) { StopPlay(); return; }
        if (_vp is not { } vp || vp.Track.Count == 0) return;
        PlayBtn.Text = "⏸";
        _playTimer = Dispatcher.CreateTimer();
        _playTimer.Interval = TimeSpan.FromMilliseconds(150);
        _playTimer.Tick += (_, _) =>
        {
            int v = _timeIdx + 1;
            if (v > (int)TimeSlider.Maximum) v = (int)TimeSlider.Minimum;
            TimeSlider.Value = v;
        };
        _playTimer.Start();
    }

    void StopPlay()
    {
        if (_playTimer != null) { _playTimer.Stop(); _playTimer = null; }
        PlayBtn.Text = "▶";
    }

    void OnChartZoomIn(object? sender, EventArgs e) => ChartZoom(0.7);
    void OnChartZoomOut(object? sender, EventArgs e) => ChartZoom(1.4);
    void OnChartFit(object? sender, EventArgs e) { if (_vp is not null) { FitWindow(); Chart.InvalidateSurface(); } }

    void OnChartBig(object? sender, EventArgs e)
    {
        _chartBig = !_chartBig;
        Chart.HeightRequest = _chartBig ? ChartHBig : ChartHNormal;
        BigBtn.Text = _chartBig ? "🗕" : "⛶";
    }

    void OnChartCollapse(object? sender, EventArgs e)
    {
        ChartBody.IsVisible = !ChartBody.IsVisible;
        CollapseBtn.Text = ChartBody.IsVisible ? "▾" : "▴";
        if (!ChartBody.IsVisible) StopPlay();
    }

    async void OnOpenAr(object? sender, EventArgs e)
    {
        if (_vp is not { } vp) return;
        StopPlay();
        await Navigation.PushAsync(new ArPage(_objLat, _objLon, _objTopZ, _obsLat, _obsLon,
            vp, _settings, DsmCache));
    }

    async void OnGpsViewpoint(object? sender, EventArgs e)
    {
        if (!_hasObj) { InfoLabel.Text = "Nejdřív vyber ① Objekt (zaměř na křížek)."; return; }
        SetBusy(true, "Zjišťuji GPS polohu…");
        try
        {
            var loc = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(12)));
            if (loc is null) { InfoLabel.Text = "GPS poloha nedostupná."; SetBusy(false); return; }
            var (mx, my) = SphericalMercator.FromLonLat(loc.Longitude, loc.Latitude);
            _map.Map.Navigator.CenterOnAndZoomTo(new MPoint(mx, my), _map.Map.Navigator.Viewport.Resolution);
            SetBusy(false);
            await PickViewpoint(loc.Latitude, loc.Longitude);
        }
        catch (Exception ex) { InfoLabel.Text = "GPS chyba: " + ex.Message; SetBusy(false); }
    }

    void OnChartPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panA0 = _winA0; _panA1 = _winA1; break;
            case GestureStatus.Running:
                double w = Chart.Width; if (w <= 0) return;
                double span = _panA1 - _panA0;
                double dAz = e.TotalX / w * span;
                _winA0 = _panA0 - dAz; _winA1 = _panA1 - dAz;
                Chart.InvalidateSurface(); break;
        }
    }

    void OnChartPinch(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _pinchA0 = _winA0; _pinchA1 = _winA1; break;
            case GestureStatus.Running:
                if (e.Scale <= 0) return;
                double span0 = _pinchA1 - _pinchA0;
                double pivot = _pinchA0 + e.ScaleOrigin.X * span0;
                double newSpan = Math.Clamp(span0 / e.Scale, 4, 360);
                double scale = newSpan / span0;
                _winA0 = pivot - (pivot - _pinchA0) * scale;
                _winA1 = pivot + (_pinchA1 - pivot) * scale;
                Chart.InvalidateSurface(); break;
        }
    }

    // ---------- horizont (SkiaSharp) ----------
    void OnPaintHorizon(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(12, 12, 18));
        if (_vp is not { } vp) return;

        int W = e.Info.Width, H = e.Info.Height;
        double a0 = _winA0, a1 = _winA1;
        if (a1 - a0 < 1e-6) return;
        double elMin = -3, elMax = 6;
        foreach (var (az, el) in vp.Horizon) if (az >= a0 && az <= a1 && el > elMax) elMax = el;
        foreach (var s in _track) if (s.Az >= a0 && s.Az <= a1 && s.Alt > elMax) elMax = s.Alt;
        elMax += 2;

        float X(double az) => (float)((az - a0) / (a1 - a0) * W);
        float Y(double el) => (float)(H - (el - elMin) / (elMax - elMin) * H);

        // silueta horizontu
        using var terr = new SKPaint { Color = new SKColor(34, 68, 51), IsAntialias = true };
        var path = new SKPath();
        path.MoveTo(0, H);
        for (double az = a0; az <= a1; az += 0.5)
            path.LineTo(X(az), Y(HorizonAt(vp.Horizon, az)));
        path.LineTo(W, H); path.Close();
        canvas.DrawPath(path, terr);

        // čára obzoru (el = 0)
        using var zero = new SKPaint { Color = new SKColor(80, 90, 110), StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(0, Y(0), W, Y(0), zero);

        // svislice na objekt + potřebná výška
        using var objp = new SKPaint { Color = new SKColor(0, 200, 255), StrokeWidth = 2, IsAntialias = true };
        if (_vpBearing >= a0 && _vpBearing <= a1) canvas.DrawLine(X(_vpBearing), 0, X(_vpBearing), H, objp);

        // dráha Měsíce (celá) jako drobné tečky
        foreach (var s in _track)
        {
            if (s.Az < a0 || s.Az > a1) continue;
            double he = HorizonAt(vp.Horizon, s.Az);
            SKColor c = s.Alt <= 0 ? new SKColor(150, 160, 180)
                : s.Alt > he ? new SKColor(255, 225, 80) : new SKColor(240, 120, 90);
            using var p = new SKPaint { Color = c, IsAntialias = true };
            canvas.DrawCircle(X(s.Az), Y(s.Alt), 2.5f, p);
        }

        // bod „na špici" (potřebná výška ve směru objektu)
        using var tipp = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        if (_vpBearing >= a0 && _vpBearing <= a1) canvas.DrawCircle(X(_vpBearing), Y(vp.ElTargetDeg), 7, tipp);

        // aktuální poloha Měsíce (dle času)
        if (_track.Count > 0 && _timeIdx < _track.Count)
        {
            var m = _track[_timeIdx];
            if (m.Az >= a0 && m.Az <= a1)
            {
                float mx = X(m.Az), my = Y(m.Alt);
                using var glow = new SKPaint { Color = new SKColor(255, 245, 200, 70), IsAntialias = true };
                canvas.DrawCircle(mx, my, 12, glow);
                using var moon = new SKPaint { Color = new SKColor(255, 240, 180), IsAntialias = true };
                canvas.DrawCircle(mx, my, 6, moon);
                using var ring = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                canvas.DrawCircle(mx, my, 6, ring);
            }
        }

        // popisky azimutu
        using var txt = new SKPaint { Color = new SKColor(150, 160, 180), IsAntialias = true };
        using var font = new SKFont { Size = 20 };
        canvas.DrawText($"{a0:0}°", 4, H - 6, SKTextAlign.Left, font, txt);
        canvas.DrawText($"{(a0 + a1) / 2:0}°", W / 2f, H - 6, SKTextAlign.Center, font, txt);
        canvas.DrawText($"{a1:0}°", W - 4, H - 6, SKTextAlign.Right, font, txt);
    }

    static double HorizonAt((double Az, double El)[] prof, double az)
    {
        const double step = 2.0;
        az = ((az % 360) + 360) % 360;
        double fi = az / step;
        int i0 = ((int)Math.Floor(fi)) % prof.Length; if (i0 < 0) i0 += prof.Length;
        int i1 = (i0 + 1) % prof.Length;
        double f = fi - Math.Floor(fi);
        return prof[i0].El * (1 - f) + prof[i1].El * f;
    }
}
