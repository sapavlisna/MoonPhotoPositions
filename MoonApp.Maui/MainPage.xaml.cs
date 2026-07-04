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
    double _meLat, _meLon, _heading;
    bool _hasMe;
    ILayer[] _bases = null!;
    static readonly string[] BaseNames = ["Mapa", "Satelit", "Ortofoto"];
    static readonly string[] BaseShort = ["OSM", "Sat", "Orto"];
    int _baseIdx;

    readonly PlannerSettings _settings = new();
    int _moonClicks;                // 🥚 easter egg: 7× klik na logo
    bool _visOnly;                  // režim „jen viditelnost objektu (bez Měsíce)"
    bool _hasObj;
    double _objLat, _objLon, _objTopZ;
    double _obsLat, _obsLon;        // poslední stanoviště
    ViewpointResult? _vp;
    double _vpBearing;

    // stav náhledu (horizont + dráha Měsíce)
    double _winA0, _winA1;          // azimutové okno [°]
    List<MoonSample> _track = [];   // dráha Měsíce
    int _timeIdx;                   // index vzorku v _track
    double _sheetDrag;              // TranslationY panelu na začátku tažení
    bool _chartBig;                 // zvětšený náhled
    double _pinchA0, _pinchA1;      // okno na začátku pinch gesta
    double _panA0, _panA1;          // okno na začátku pan gesta
    IDispatcherTimer? _playTimer;

    const double ChartHNormal = 190, ChartHBig = 460;

    // perzistentní cache výškopisu (přežije čištění systémové cache → offline po stažení)
    static string DsmCache => Path.Combine(FileSystem.AppDataDirectory, "dsm");

    // Směrový kužel GPS polohy (SVG s gradientem) — apex uprostřed, otáčí se kompasem.
    static string MeConePath => Path.Combine(FileSystem.AppDataDirectory, "mecone.svg");
    static bool _coneReady;
    static string MeConeUri()
    {
        if (!_coneReady || !File.Exists(MeConePath))
        {
            File.WriteAllText(MeConePath,
                "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
                "<defs><linearGradient id='g' x1='0' y1='0' x2='0' y2='1'>" +
                "<stop offset='0' stop-color='#38BDF8' stop-opacity='0'/>" +
                "<stop offset='0.55' stop-color='#38BDF8' stop-opacity='0.35'/>" +
                "<stop offset='1' stop-color='#38BDF8' stop-opacity='0.85'/>" +
                "</linearGradient></defs>" +
                "<path d='M50 50 L18 8 Q50 -2 82 8 Z' fill='url(#g)'/></svg>");
            _coneReady = true;
        }
        return new Uri(MeConePath).AbsoluteUri;
    }

    // theme-aware barva (honoruje UserAppTheme override)
    static Microsoft.Maui.Graphics.Color ThemeCol(string light, string dark) =>
        Microsoft.Maui.Graphics.Color.FromArgb(
            Application.Current?.RequestedTheme == AppTheme.Dark ? dark : light);

    public MainPage()
    {
        InitializeComponent();
        BuildMap();
        InitChart();
        UpdateDateLabelTop();
        UpdateStepper();
    }

    void UpdateDateLabelTop() =>
        DateLabelTop.Text = _settings.Date.ToString("ddd d.M.",
            System.Globalization.CultureInfo.GetCultureInfo("cs-CZ"));

    // 🥚 7× klik na logo → pokrytí počítá jen viditelnost objektu z okolí (bez Měsíce).
    void OnLogoTap(object? sender, TappedEventArgs e)
    {
        if (++_moonClicks < 7) return;
        _moonClicks = 0;
        _visOnly = !_visOnly;
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        LogoImg.Source = _visOnly
            ? (dark ? "ic_eye_d.png" : "ic_eye_l.png")
            : (dark ? "ic_moon_d.png" : "ic_moon_l.png");
        ShowAnswerUi(false);
        InfoLabel.IsVisible = true;
        InfoLabel.Text = _visOnly
            ? "🥚 Režim: jen viditelnost objektu z okolí (bez Měsíce). Přepočítej ① Objekt."
            : "Zpět k běžnému pokrytí (s Měsícem). Přepočítej ① Objekt.";
    }

    async void OnOpenDatePicker(object? sender, TappedEventArgs e)
    {
        var page = new DatePickerPage(_settings.Date);
        await Navigation.PushModalAsync(page);
        var picked = await page.PickAsync();
        if (picked is { } d) ApplyDate(d);
    }

    void ApplyDate(DateTime date)
    {
        _settings.Date = date;
        UpdateDateLabelTop();
        _vp = null; StopPlay(); ShowAnswerUi(false); UpdateStepper();
        InfoLabel.IsVisible = true;
        InfoLabel.Text = $"Datum {_settings.Date:d.M.yyyy}. Přepočítej ① Objekt / ② Stanoviště pro nový den.";
    }

    bool _updateChecked;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateDateLabelTop();
        _ = StartLocationAsync();
        StartCompass();
        if (!_updateChecked) { _updateChecked = true; _ = UpdateService.CheckAsync(this, manual: false); }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopCompass();
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

    void StartCompass()
    {
        if (!Compass.Default.IsSupported || Compass.Default.IsMonitoring) return;
        Compass.Default.ReadingChanged += OnCompass;
        Compass.Default.Start(SensorSpeed.UI, applyLowPassFilter: true);
    }

    void StopCompass()
    {
        if (Compass.Default.IsMonitoring)
        {
            Compass.Default.Stop();
            Compass.Default.ReadingChanged -= OnCompass;
        }
    }

    void OnCompass(object? sender, CompassChangedEventArgs e)
    {
        double h = e.Reading.HeadingMagneticNorth;
        if (Math.Abs(((h - _heading + 540) % 360) - 180) < 3) return;   // throttle na ~3°
        _heading = h;
        if (_hasMe) RedrawMe();
    }

    void UpdateMe(Location loc)
    {
        _meLat = loc.Latitude; _meLon = loc.Longitude; _hasMe = true;
        RedrawMe();
    }

    void RedrawMe()
    {
        var (x, y) = SphericalMercator.FromLonLat(_meLon, _meLat);
        Remove(ref _meLayer);
        var pf = new PointFeature(new MPoint(x, y));
        // accuracy prstenec (spodní vrstva)
        pf.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(56, 189, 248, 45)),
            Outline = new Pen(new Mapsui.Styles.Color(56, 189, 248, 90), 1),
            SymbolScale = 1.6,
        });
        // směrový kužel (kompas) — poloprůhledný gradient jako v mapových appkách
        pf.Styles.Add(new ImageStyle
        {
            Image = MeConeUri(),
            SymbolScale = 1.1,
            SymbolRotation = _heading,
        });
        // světle modrá tečka polohy (#38BDF8)
        pf.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(56, 189, 248)),
            Outline = new Pen(new Mapsui.Styles.Color(255, 255, 255), 3),
            SymbolScale = 0.5,
        });
        pf.Styles.Add(ChipLabel("Moje poloha"));
        _meLayer = new MemoryLayer("me") { Features = [pf], Style = null };
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
        var tap = new TapGestureRecognizer();
        tap.Tapped += OnChartTap;
        Chart.GestureRecognizers.Add(tap);
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
        LayerBtn.Text = BaseShort[0];
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

    void OnToggleLayer(object? sender, TappedEventArgs e)
    {
        _map.Map.Layers.Remove(_bases[_baseIdx]);
        _baseIdx = (_baseIdx + 1) % _bases.Length;
        _map.Map.Layers.Insert(0, _bases[_baseIdx]);       // pod overlaye
        LayerBtn.Text = BaseShort[_baseIdx];
        _map.Refresh();
    }

    async void OnSettings(object? sender, TappedEventArgs e)
        => await Navigation.PushAsync(new SettingsPage(_settings));

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
                dMin: Math.Max(80, _settings.RadiusMinM), visOnly: _visOnly, cacheDir: cache, progress: progress));
            DrawCoverage(g, snap);
            ViewBtn.IsEnabled = true;
            _vp = null; StopPlay(); ShowAnswerUi(false);
            UpdateStepper();
            InfoLabel.IsVisible = true;
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
            SetupChartWindow(r);
            string tip = r.OnTipUtc is { } t
                ? TimeZoneInfo.ConvertTimeFromUtc(t, Time.Prague).ToString("HH:mm")
                : "—";

            // answer-first blok
            ShowAnswerUi(true);
            UpdateStepper();
            InfoLabel.IsVisible = false;
            ChartBody.IsVisible = false;          // graf sbalený, otevře se přes rozbalovač
            GraphChevron.Text = "▾";
            MetaLbl.Text = $"{lat:0.#####}, {lon:0.#####}  ·  {r.DistanceM:0} m  ·  {r.Bearing:0}°";

            if (r.Clear)
            {
                AnswerPanel.BackgroundColor = ThemeCol("#ECFDF3", "#10261A");
                AnswerIconBox.BackgroundColor = ThemeCol("#16A34A", "#34D399");
                AnswerIconImg.Source = "ic_target.png";
                AnswerEyebrow.Text = "MĚSÍC NA ŠPICI";
                AnswerEyebrow.TextColor = ThemeCol("#16A34A", "#34D399");
                AnswerTime.FontFamily = "monospace";
                AnswerTime.FontSize = tip == "—" ? 22 : 32;
                AnswerTime.Text = tip == "—" ? "nevyjde dnes" : tip;
                StatusColumn.IsVisible = true;
                StatusLbl.Text = "✓ Vidět";
                StatusLbl.TextColor = ThemeCol("#16A34A", "#34D399");
                StatusSub.Text = $"potř. výška {r.ElTargetDeg:0.0}°";
                if (tip == "—")
                {
                    HintBox.IsVisible = true;
                    HintLbl.Text = "⚠  Tento den Měsíc na špici objektu nevyjde — zkus posuvník data v grafu.";
                }
                else HintBox.IsVisible = false;
            }
            else
            {
                AnswerPanel.BackgroundColor = ThemeCol("#FEECEC", "#2A1416");
                AnswerIconBox.BackgroundColor = ThemeCol("#DC2626", "#F87171");
                AnswerIconImg.Source = "ic_ban.png";
                AnswerEyebrow.Text = "OBJEKT ZAKRYTÝ TERÉNEM";
                AnswerEyebrow.TextColor = ThemeCol("#DC2626", "#F87171");
                AnswerTime.FontFamily = "OpenSansSemibold";
                AnswerTime.FontSize = 22;
                AnswerTime.Text = "Odsud Měsíc nevyjde";
                StatusColumn.IsVisible = false;
                HintBox.IsVisible = true;
                HintLbl.Text = $"⚠  Posuň stanoviště dál nebo zkus jiné datum — potřebná výška {r.ElTargetDeg:0.0}° je nad terénem.";
            }
        }
        catch (Exception ex) { InfoLabel.Text = "Chyba: " + ex.Message; }
        finally { SetBusy(false); }
    }

    // Zobrazí/skryje answer-first blok (odpověď + meta + rozbalovač grafu).
    void ShowAnswerUi(bool show)
    {
        AnswerPanel.IsVisible = show;
        MetaLbl.IsVisible = show;
        GraphToggle.IsVisible = show;
        if (!show)
        {
            ChartBody.IsVisible = false;
            HintBox.IsVisible = false;
        }
    }

    // Vizuální stav krokovače ① / ②.
    void UpdateStepper()
    {
        var accent = ThemeCol("#2563EB", "#4F8EF7");
        PickBtn.Text = _hasObj ? "✓ ① Objekt" : "① Objekt";

        if (_vp != null)
        {
            ViewBtn.Text = "✓ ② Stanoviště";
            ViewBtn.BackgroundColor = accent;
            ViewBtn.TextColor = Colors.White;
            ViewBtn.BorderWidth = 0;
        }
        else if (_hasObj)
        {
            ViewBtn.Text = "② Stanoviště";
            ViewBtn.BackgroundColor = Colors.Transparent;
            ViewBtn.TextColor = accent;
            ViewBtn.BorderColor = accent;
            ViewBtn.BorderWidth = 1.5;
        }
        else
        {
            ViewBtn.Text = "② Stanoviště";
            ViewBtn.BackgroundColor = Colors.Transparent;
            ViewBtn.TextColor = ThemeCol("#64748B", "#94A3B0");
            ViewBtn.BorderColor = ThemeCol("#E2E7EE", "#263345");
            ViewBtn.BorderWidth = 1.5;
        }
    }

    void SetBusy(bool busy, string? msg = null)
    {
        Busy.IsRunning = busy;
        Busy.IsVisible = busy;
        if (busy) { ShowAnswerUi(false); InfoLabel.IsVisible = true; }
        else { Bar.IsVisible = false; Bar.Progress = 0; }
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
        _objLayer = Dot(snap.Lon, snap.Lat, new Mapsui.Styles.Color(239, 68, 68), "object", "Objekt");
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
            Style = new VectorStyle { Line = new Pen(new Mapsui.Styles.Color(37, 99, 235), 3) },
        };
        map.Layers.Add(_lineLayer);
        _obsLayer = Dot(lon, lat, new Mapsui.Styles.Color(30, 64, 175), "obs", "Stanoviště");
        map.Layers.Add(_obsLayer);
        BringMeToFront();
        _map.Refresh();
    }

    static MemoryLayer Dot(double lon, double lat, Mapsui.Styles.Color c, string name, string? label = null)
    {
        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        var pf = new PointFeature(new MPoint(x, y));
        pf.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            Fill = new Mapsui.Styles.Brush(c),
            Outline = new Pen(new Mapsui.Styles.Color(255, 255, 255), 2),
            SymbolScale = 0.6,
        });
        if (label != null) pf.Styles.Add(ChipLabel(label));
        return new MemoryLayer(name) { Features = [pf], Style = null };
    }

    // Tmavý „chip" popisek nad markerem.
    static LabelStyle ChipLabel(string text) => new()
    {
        Text = text,
        ForeColor = new Mapsui.Styles.Color(255, 255, 255),
        BackColor = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(17, 24, 39, 235)),
        Halo = new Pen(new Mapsui.Styles.Color(0, 0, 0, 60), 1),
        Font = new Mapsui.Styles.Font { Size = 12, Bold = true },
        HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Center,
        VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Bottom,
        Offset = new Offset(0, -14),
    };

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
        InvalidateChartBg();
        FitWindow();
        _timeIdx = DefaultIdx();
        TimeSlider.Maximum = Math.Max(0, _track.Count - 1);
        TimeSlider.Value = _timeIdx;   // → OnTimeChanged
        UpdateMoon();
    }

    int DefaultIdx()
    {
        if (_vp is { } vp && vp.OnTipUtc is { } tip)
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
        // Vynuceně mimo dotykový cyklus posuvníku — Android jinak překreslení plátna odkládá.
        Dispatcher.Dispatch(() => Chart.InvalidateSurface());
    }

    void OnTimeChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_track.Count == 0) return;
        _timeIdx = Math.Clamp((int)Math.Round(e.NewValue), 0, _track.Count - 1);
        UpdateMoon();
    }

    // Klik do grafu → nastaví čas na vzorek dráhy nejbližší kliknutému azimutu (X).
    void OnChartTap(object? sender, TappedEventArgs e)
    {
        if (_vp is null || _track.Count == 0 || Chart.Width <= 0) return;
        if (e.GetPosition(Chart) is not { } p) return;
        double frac = Math.Clamp(p.X / Chart.Width, 0, 1);
        double az = _winA0 + frac * (_winA1 - _winA0);

        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < _track.Count; i++)
        {
            if (_track[i].Az < _winA0 || _track[i].Az > _winA1) continue;
            double d = Math.Abs(_track[i].Az - az);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return;
        _timeIdx = best;
        TimeSlider.Value = best;   // posune spodní posuvník (a přes OnTimeChanged i graf)
        UpdateMoon();              // jistota překreslení i když se hodnota nezměnila
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

    void OnChartCollapse(object? sender, TappedEventArgs e)
    {
        ChartBody.IsVisible = !ChartBody.IsVisible;
        GraphChevron.Text = ChartBody.IsVisible ? "▴" : "▾";
        if (!ChartBody.IsVisible) StopPlay();
        else Chart.InvalidateSurface();
    }

    // Tažení spodního panelu úchytem: dolů = schovat (odkryje mapu), nahoru = zobrazit.
    // Po schování zůstane viditelný pruh s úchytem (peek), aby šel snadno vytáhnout zpět.
    const double SheetPeek = 42;
    double SheetHideMax => Math.Max(0, Sheet.Height - SheetPeek);

    void OnSheetPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _sheetDrag = Sheet.TranslationY;
                break;
            case GestureStatus.Running:
                Sheet.TranslationY = Math.Clamp(_sheetDrag + e.TotalY, 0, SheetHideMax);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                double max = SheetHideMax;
                double target = Sheet.TranslationY > max * 0.4 ? max : 0;
                Sheet.TranslateTo(0, target, 160, Easing.CubicOut);
                break;
        }
    }

    void OnHandleTap(object? sender, TappedEventArgs e)
    {
        double target = Sheet.TranslationY > 1 ? 0 : SheetHideMax;
        Sheet.TranslateTo(0, target, 160, Easing.CubicOut);
    }

    async void OnOpenAr(object? sender, EventArgs e)
    {
        if (_vp is not { } vp) return;
        StopPlay();
        await Navigation.PushAsync(new ArPage(_objLat, _objLon, _objTopZ, _obsLat, _obsLon,
            vp, _settings, DsmCache));
    }

    async void OnCenterOnMe(object? sender, EventArgs e)
    {
        Location? loc = _hasMe ? new Location(_meLat, _meLon) : null;
        if (loc is null)
        {
            try
            {
                loc = await Geolocation.Default.GetLocationAsync(
                    new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10)));
            }
            catch { /* níže ošetřeno */ }
        }
        if (loc is null) { InfoLabel.Text = "GPS poloha zatím není k dispozici."; return; }
        var (mx, my) = SphericalMercator.FromLonLat(loc.Longitude, loc.Latitude);
        _map.Map.Navigator.CenterOnAndZoomTo(new MPoint(mx, my), _map.Map.Navigator.Viewport.Resolution, 400);
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
    // Statická část grafu (terén, dráha, svislice, popisky) se cachuje do SKPicture
    // a překresluje jen při změně výřezu/velikosti/dat. Posun času pak jen dokreslí Měsíc.
    SKPicture? _chartBg;
    int _bgW, _bgH;
    double _bgA0 = double.NaN, _bgA1 = double.NaN, _bgElMax = 8;

    void InvalidateChartBg() { _chartBg?.Dispose(); _chartBg = null; }

    void OnPaintHorizon(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(10, 14, 23));      // #0A0E17
        if (_vp is not { } vp) return;

        int W = e.Info.Width, H = e.Info.Height;
        double a0 = _winA0, a1 = _winA1;
        if (a1 - a0 < 1e-6) return;

        if (_chartBg is null || _bgW != W || _bgH != H || _bgA0 != a0 || _bgA1 != a1)
            RecordChartBg(vp, W, H, a0, a1);
        if (_chartBg is not null) canvas.DrawPicture(_chartBg);

        // aktuální poloha Měsíce (dle času) — jediná část kreslená každý snímek
        const double elMin = -3;
        double elMax = _bgElMax;
        if (_track.Count > 0 && _timeIdx < _track.Count)
        {
            var m = _track[_timeIdx];
            if (m.Az >= a0 && m.Az <= a1)
            {
                float mx = (float)((m.Az - a0) / (a1 - a0) * W);
                float my = (float)(H - (m.Alt - elMin) / (elMax - elMin) * H);
                using var guide = new SKPaint { Color = new SKColor(255, 245, 200, 90), StrokeWidth = 1.5f, IsAntialias = true };
                canvas.DrawLine(mx, 0, mx, my, guide);
                using var glow = new SKPaint { Color = new SKColor(255, 240, 180, 90), IsAntialias = true };
                canvas.DrawCircle(mx, my, 18, glow);
                using var glow2 = new SKPaint { Color = new SKColor(255, 240, 180, 130), IsAntialias = true };
                canvas.DrawCircle(mx, my, 11, glow2);
                using var moon = new SKPaint { Color = new SKColor(255, 244, 205), IsAntialias = true };
                canvas.DrawCircle(mx, my, 8, moon);
                using var ring = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
                canvas.DrawCircle(mx, my, 8.5f, ring);
            }
        }
    }

    void RecordChartBg(ViewpointResult vp, int W, int H, double a0, double a1)
    {
        double elMin = -3, elMax = 6;
        foreach (var (az, el) in vp.Horizon) if (az >= a0 && az <= a1 && el > elMax) elMax = el;
        foreach (var s in _track) if (s.Az >= a0 && s.Az <= a1 && s.Alt > elMax) elMax = s.Alt;
        elMax += 2;
        _bgW = W; _bgH = H; _bgA0 = a0; _bgA1 = a1; _bgElMax = elMax;

        float X(double az) => (float)((az - a0) / (a1 - a0) * W);
        float Y(double el) => (float)(H - (el - elMin) / (elMax - elMin) * H);

        using var rec = new SKPictureRecorder();
        var canvas = rec.BeginRecording(new SKRect(0, 0, W, H));

        // silueta horizontu (#16351F)
        using var terr = new SKPaint { Color = new SKColor(22, 53, 31), IsAntialias = true };
        var path = new SKPath();
        path.MoveTo(0, H);
        for (double az = a0; az <= a1; az += 0.5)
            path.LineTo(X(az), Y(HorizonAt(vp.Horizon, az)));
        path.LineTo(W, H); path.Close();
        canvas.DrawPath(path, terr);

        // čára obzoru (el = 0)
        using var zero = new SKPaint { Color = new SKColor(60, 72, 90), StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(0, Y(0), W, Y(0), zero);

        // svislice na objekt + potřebná výška (#38BDF8)
        using var objp = new SKPaint { Color = new SKColor(56, 189, 248), StrokeWidth = 2, IsAntialias = true };
        if (_vpBearing >= a0 && _vpBearing <= a1) canvas.DrawLine(X(_vpBearing), 0, X(_vpBearing), H, objp);

        // dráha Měsíce: žlutá #FACC15 (vidět) · oranžová #F97316 (za překážkou) · šedá (pod obzorem)
        foreach (var s in _track)
        {
            if (s.Az < a0 || s.Az > a1) continue;
            double he = HorizonAt(vp.Horizon, s.Az);
            SKColor c = s.Alt <= 0 ? new SKColor(124, 134, 152)
                : s.Alt > he ? new SKColor(250, 204, 21) : new SKColor(249, 115, 22);
            using var p = new SKPaint { Color = c, IsAntialias = true };
            canvas.DrawCircle(X(s.Az), Y(s.Alt), 2.5f, p);
        }

        // bod „na špici" (potřebná výška ve směru objektu)
        using var tipp = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        if (_vpBearing >= a0 && _vpBearing <= a1) canvas.DrawCircle(X(_vpBearing), Y(vp.ElTargetDeg), 7, tipp);

        // popisky azimutu
        using var txt = new SKPaint { Color = new SKColor(124, 134, 152), IsAntialias = true };
        using var font = new SKFont { Size = 20 };
        canvas.DrawText($"{a0:0}°", 4, H - 6, SKTextAlign.Left, font, txt);
        canvas.DrawText($"{(a0 + a1) / 2:0}°", W / 2f, H - 6, SKTextAlign.Center, font, txt);
        canvas.DrawText($"{a1:0}°", W - 4, H - 6, SKTextAlign.Right, font, txt);

        _chartBg?.Dispose();
        _chartBg = rec.EndRecording();
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
