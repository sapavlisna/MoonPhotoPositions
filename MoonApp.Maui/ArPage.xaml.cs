using System.Numerics;
using MoonApp.Core;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace MoonApp.Maui;

public partial class ArPage : ContentPage
{
    readonly double _objLat, _objLon, _objTopZ;
    readonly PlannerSettings _settings;
    readonly string _cacheDir;
    double _obsLat, _obsLon;

    SKCanvasView _overlay = null!;

    ViewpointResult _vp;
    List<MoonSample> _track;
    (double Az, double El)[] _horizon;
    double _bearing, _elTarget;
    int _timeIdx;

    // orientace
    double _rawAz, _rawPitch;          // ze senzoru (vyhlazené)
    bool _hasReading;                  // první čtení → bez vyhlazení
    double _offAz, _offPitch;          // kalibrace (kompas mód)
    double _fingerAz, _fingerPitch;    // prstový mód
    bool _fingerMode;
    bool _cameraOn;
    double _overlayAlpha = 0.55;       // průhlednost překryvu nad kamerou
    double _nowAz, _nowAlt;            // aktuální (reálná) poloha Měsíce
    IDispatcherTimer? _nowTimer;
    const double HFov = 60.0;
    const double SmoothK = 0.15;       // vyhlazení kompasu (nižší = klidnější)

    public ArPage(double objLat, double objLon, double objTopZ,
        double obsLat, double obsLon, ViewpointResult vp, PlannerSettings settings, string cacheDir)
    {
        InitializeComponent();
        _objLat = objLat; _objLon = objLon; _objTopZ = objTopZ;
        _obsLat = obsLat; _obsLon = obsLon;
        _settings = settings; _cacheDir = cacheDir;
        _vp = vp; _track = [.. vp.Track]; _horizon = vp.Horizon;
        _bearing = vp.Bearing; _elTarget = vp.ElTargetDeg;
        _fingerAz = _bearing; _fingerPitch = Math.Max(0, _elTarget);

        _overlay = new SKCanvasView { InputTransparent = true };
        _overlay.PaintSurface += OnPaint;
        Root.Children.Insert(1, _overlay);   // nad CameraView (index 0)

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPan;
        Root.GestureRecognizers.Add(pan);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        TimeSlider.Maximum = Math.Max(0, _track.Count - 1);
        _timeIdx = DefaultTimeIdx();
        TimeSlider.Value = _timeIdx;
        UpdateTimeLabel();

        RefreshNowMoon();
        _nowTimer = Dispatcher.CreateTimer();
        _nowTimer.Interval = TimeSpan.FromSeconds(20);
        _nowTimer.Tick += (_, _) => RefreshNowMoon();
        _nowTimer.Start();

        if (OrientationSensor.Default.IsSupported)
        {
            OrientationSensor.Default.ReadingChanged += OnOrientation;
            OrientationSensor.Default.Start(SensorSpeed.Game);
        }
        else
        {
            _fingerMode = true;
            MoveBtn.Text = "👆 Prst";
            Status.Text = "Senzor orientace není dostupný — ovládej prstem.";
        }
        UpdateStatus();
        _overlay.InvalidateSurface();
    }

    void RefreshNowMoon()
    {
        var m = Astro.MoonAt(_obsLat, _obsLon, DateTime.UtcNow);
        _nowAz = m.Az; _nowAlt = m.Alt;
        _overlay.InvalidateSurface();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (OrientationSensor.Default.IsMonitoring)
        {
            OrientationSensor.Default.Stop();
            OrientationSensor.Default.ReadingChanged -= OnOrientation;
        }
        if (_cameraOn) { try { Cam.StopCameraPreview(); } catch { } }
        _nowTimer?.Stop(); _nowTimer = null;
    }

    int DefaultTimeIdx()
    {
        if (_vp.OnTipUtc is { } tip)
            for (int i = 0; i < _track.Count; i++) if (_track[i].TimeUtc == tip) return i;
        for (int i = 0; i < _track.Count; i++)
            if (_track[i].Alt > HorizonAt(_track[i].Az)) return i;
        return _track.Count / 2;
    }

    double Heading => _fingerMode ? _fingerAz : (_rawAz + _offAz);
    double Pitch => _fingerMode ? _fingerPitch : (_rawPitch + _offPitch);

    void OnOrientation(object? sender, OrientationSensorChangedEventArgs e)
    {
        Quaternion q = new(e.Reading.Orientation.X, e.Reading.Orientation.Y,
            e.Reading.Orientation.Z, e.Reading.Orientation.W);
        var dir = Vector3.Transform(new Vector3(0, 0, -1), q);
        double naz = (Math.Atan2(dir.X, dir.Y) * 180.0 / Math.PI + 360) % 360;
        double npitch = Math.Asin(Math.Clamp(dir.Z, -1f, 1f)) * 180.0 / Math.PI;
        if (!_hasReading) { _rawAz = naz; _rawPitch = npitch; _hasReading = true; }
        else
        {
            // exponenciální vyhlazení; azimut přes nejkratší úhlový rozdíl (0/360 wrap)
            double d = ((naz - _rawAz + 540) % 360) - 180;
            _rawAz = ((_rawAz + SmoothK * d) % 360 + 360) % 360;
            _rawPitch += SmoothK * (npitch - _rawPitch);
        }
        if (!_fingerMode) _overlay.InvalidateSurface();
    }

    void OnPan(object? sender, PanUpdatedEventArgs e)
    {
        if (!_fingerMode) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panAz = _fingerAz; _panPitch = _fingerPitch; break;
            case GestureStatus.Running:
                double w = _overlay.Width, h = _overlay.Height;
                if (w <= 0 || h <= 0) return;
                double vFov = HFov * h / w;
                _fingerAz = ((_panAz - e.TotalX / w * HFov) % 360 + 360) % 360;
                _fingerPitch = Math.Clamp(_panPitch + e.TotalY / h * vFov, -90, 90);
                _overlay.InvalidateSurface(); break;
        }
    }
    double _panAz, _panPitch;

    void OnTimeChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_track.Count == 0) return;
        _timeIdx = Math.Clamp((int)Math.Round(e.NewValue), 0, _track.Count - 1);
        UpdateTimeLabel();
        _overlay.InvalidateSurface();
    }

    void UpdateTimeLabel()
    {
        if (_track.Count == 0) { TimeLbl.Text = "🕒 —"; return; }
        var s = _track[_timeIdx];
        var local = TimeZoneInfo.ConvertTimeFromUtc(s.TimeUtc, Time.Prague);
        double he = HorizonAt(s.Az);
        string st = s.Alt <= 0 ? "pod obzorem" : s.Alt > he ? "nad obzorem (vidět)" : "za překážkou";
        TimeLbl.Text = $"🕒 {local:HH:mm} — az {s.Az:0.0}°, alt {s.Alt:0.0}° · {st}";
    }

    void OnToggleMove(object? sender, EventArgs e)
    {
        _fingerMode = !_fingerMode;
        if (_fingerMode) { _fingerAz = Heading; _fingerPitch = Pitch; MoveBtn.Text = "👆 Prst"; }
        else MoveBtn.Text = "🧭 Kompas";
        UpdateStatus();
        _overlay.InvalidateSurface();
    }

    async void OnToggleCamera(object? sender, EventArgs e)
    {
        if (!_cameraOn)
        {
            var st = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (st != PermissionStatus.Granted) st = await Permissions.RequestAsync<Permissions.Camera>();
            if (st != PermissionStatus.Granted) { Status.Text = "Bez povolení kamery — zůstává grafika."; return; }
            try
            {
                Cam.IsVisible = true;
                await Cam.StartCameraPreview(CancellationToken.None);
                _cameraOn = true;
                CamBtn.Text = "📷 Kamera";
                OverlayCtl.IsVisible = true;
            }
            catch (Exception ex)
            {
                Status.Text = "Kamera se nespustila: " + ex.Message;
                Cam.IsVisible = false;
            }
        }
        else
        {
            try { Cam.StopCameraPreview(); } catch { }
            Cam.IsVisible = false;
            _cameraOn = false;
            CamBtn.Text = "🎨 Grafika";
            OverlayCtl.IsVisible = false;
            UpdateStatus();
        }
        _overlay.InvalidateSurface();
    }

    void OnOverlayChanged(object? sender, ValueChangedEventArgs e)
    {
        _overlayAlpha = e.NewValue;
        _overlay.InvalidateSurface();
    }

    void OnCalibrate(object? sender, EventArgs e)
    {
        if (_fingerMode) { Status.Text = "Kalibrace jen v režimu Kompas."; return; }
        var m = _track.Count > 0 ? _track[_timeIdx] : default;
        _offAz = ((m.Az - _rawAz + 540) % 360) - 180;
        _offPitch = m.Alt - _rawPitch;
        Status.Text = $"Zkalibrováno na Měsíc (Δaz {_offAz:0.0}°, Δalt {_offPitch:0.0}°).";
        _overlay.InvalidateSurface();
    }

    async void OnGps(object? sender, EventArgs e)
    {
        Status.Text = "Zjišťuji GPS polohu…";
        try
        {
            var loc = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(12)));
            if (loc is null) { Status.Text = "GPS poloha nedostupná."; return; }
            Status.Text = "Přepočítávám stanoviště z GPS…";
            var r = await Task.Run(() => Planner.ViewpointAsync(_objLat, _objLon, _objTopZ,
                loc.Latitude, loc.Longitude, DateOnly.FromDateTime(_settings.Date),
                _settings.EyeH, cacheDir: _cacheDir));
            _vp = r; _track = [.. r.Track]; _horizon = r.Horizon;
            _bearing = r.Bearing; _elTarget = r.ElTargetDeg;
            _obsLat = loc.Latitude; _obsLon = loc.Longitude;
            TimeSlider.Maximum = Math.Max(0, _track.Count - 1);
            _timeIdx = DefaultTimeIdx(); TimeSlider.Value = _timeIdx;
            UpdateTimeLabel(); UpdateStatus();
            _overlay.InvalidateSurface();
        }
        catch (Exception ex) { Status.Text = "GPS chyba: " + ex.Message; }
    }

    void UpdateStatus()
    {
        string mode = _fingerMode ? "prst" : "kompas";
        string vis = _vp.Clear ? "✅ objekt vidět" : "⛔ objekt zakrytý";
        Status.Text = $"Stanoviště {_obsLat:0.####}, {_obsLon:0.####} · {mode} · {vis} · " +
                      $"objekt az {_bearing:0}°, potřebná výška {_elTarget:0.0}°";
    }

    async void OnClose(object? sender, EventArgs e) => await Navigation.PopAsync();

    double HorizonAt(double az)
    {
        var prof = _horizon;
        if (prof.Length == 0) return 0;
        double step = 360.0 / prof.Length;
        az = ((az % 360) + 360) % 360;
        double fi = az / step;
        int i0 = ((int)Math.Floor(fi)) % prof.Length; if (i0 < 0) i0 += prof.Length;
        int i1 = (i0 + 1) % prof.Length;
        double f = fi - Math.Floor(fi);
        return prof[i0].El * (1 - f) + prof[i1].El * f;
    }

    void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        int W = e.Info.Width, H = e.Info.Height;
        double heading = Heading, pitch = Pitch;
        double vFov = HFov * H / W;

        if (_cameraOn) c.Clear(SKColors.Transparent);
        else DrawSky(c, W, H, pitch, vFov);

        // terén (silueta horizontu) — projekce po 0.5°
        DrawTerrain(c, W, H, heading, pitch, vFov);

        // dráha Měsíce (celý track)
        DrawTrack(c, W, H, heading, pitch, vFov);

        // objekt: svislice + potřebná výška
        var (ox, oy, oIn) = Ar.Project(_bearing, _elTarget, heading, pitch, HFov, vFov, W, H);
        if (Math.Abs(((_bearing - heading + 540) % 360) - 180) < HFov / 2)
        {
            using var op = new SKPaint { Color = new SKColor(0, 200, 255), StrokeWidth = 4, IsAntialias = true };
            c.DrawLine((float)ox, (float)oy, (float)ox, H, op);
            using var ot = new SKPaint { Color = new SKColor(0, 200, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
            c.DrawCircle((float)ox, (float)oy, 13, ot);
        }

        // plánovaný Měsíc (dle časového posuvníku) — obrysový kroužek + šipka když mimo
        if (_track.Count > 0 && _timeIdx < _track.Count)
        {
            var m = _track[_timeIdx];
            var (mx, my, mIn) = Ar.Project(m.Az, m.Alt, heading, pitch, HFov, vFov, W, H);
            if (mIn)
            {
                using var ring = new SKPaint { Color = new SKColor(255, 255, 255, 230), Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
                c.DrawCircle((float)mx, (float)my, 17, ring);
                using var lt = new SKPaint { Color = new SKColor(210, 225, 255), IsAntialias = true };
                using var lf = new SKFont { Size = 22 };
                c.DrawText("plán", (float)mx, (float)my - 24, SKTextAlign.Center, lf, lt);
            }
            else DrawMoonArrow(c, W, H, m, heading, pitch, vFov);
        }

        // aktuální (reálná) poloha Měsíce — plný žlutý kotouč
        if (_nowAlt > -5)
        {
            var (nx, ny, nIn) = Ar.Project(_nowAz, _nowAlt, heading, pitch, HFov, vFov, W, H);
            if (nIn)
            {
                using var glow = new SKPaint { Color = new SKColor(255, 245, 200, 80), IsAntialias = true };
                c.DrawCircle((float)nx, (float)ny, 34, glow);
                using var moon = new SKPaint { Color = new SKColor(255, 240, 175), IsAntialias = true };
                c.DrawCircle((float)nx, (float)ny, 20, moon);
                using var nt = new SKPaint { Color = new SKColor(255, 240, 175), IsAntialias = true };
                using var nf = new SKFont { Size = 22 };
                c.DrawText("teď", (float)nx, (float)ny + 42, SKTextAlign.Center, nf, nt);
            }
        }

        // zaměřovač
        using var cross = new SKPaint { Color = new SKColor(255, 255, 255, 120), StrokeWidth = 2, IsAntialias = true };
        c.DrawLine(W / 2f - 24, H / 2f, W / 2f + 24, H / 2f, cross);
        c.DrawLine(W / 2f, H / 2f - 24, W / 2f, H / 2f + 24, cross);
    }

    static void DrawSky(SKCanvas c, int W, int H, double pitch, double vFov)
    {
        using var sky = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H),
                [new SKColor(10, 14, 34), new SKColor(28, 38, 66)], null, SKShaderTileMode.Clamp),
        };
        c.DrawRect(0, 0, W, H, sky);
    }

    void DrawTerrain(SKCanvas c, int W, int H, double heading, double pitch, double vFov)
    {
        var path = new SKPath();
        bool started = false;
        for (double d = -HFov / 2; d <= HFov / 2 + 1e-6; d += 0.5)
        {
            double az = heading + d;
            var (x, y, _) = Ar.Project(az, HorizonAt(az), heading, pitch, HFov, vFov, W, H);
            if (!started) { path.MoveTo((float)x, (float)y); started = true; }
            else path.LineTo((float)x, (float)y);
        }
        path.LineTo(W, H); path.LineTo(0, H); path.Close();
        byte terrA = (byte)(_cameraOn ? Math.Clamp(_overlayAlpha, 0, 1) * 255 : 255);
        using var terr = new SKPaint { Color = new SKColor(24, 42, 32, terrA), IsAntialias = true };
        c.DrawPath(path, terr);
        using var edge = new SKPaint { Color = new SKColor(60, 120, 90), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        c.DrawPath(path, edge);
    }

    void DrawTrack(SKCanvas c, int W, int H, double heading, double pitch, double vFov)
    {
        SKPoint? prev = null;
        foreach (var s in _track)
        {
            double daz = ((s.Az - heading + 540) % 360) - 180;
            if (Math.Abs(daz) > HFov) { prev = null; continue; }
            var (x, y, _) = Ar.Project(s.Az, s.Alt, heading, pitch, HFov, vFov, W, H);
            double he = HorizonAt(s.Az);
            SKColor col = s.Alt <= 0 ? new SKColor(150, 160, 180, 180)
                : s.Alt > he ? new SKColor(255, 225, 80) : new SKColor(240, 120, 90);
            var pt = new SKPoint((float)x, (float)y);
            if (prev is { } p)
            {
                using var lp = new SKPaint { Color = col, StrokeWidth = 3, IsAntialias = true };
                c.DrawLine(p, pt, lp);
            }
            prev = pt;
        }
    }

    void DrawMoonArrow(SKCanvas c, int W, int H, MoonSample m, double heading, double pitch, double vFov)
    {
        double daz = ((m.Az - heading + 540) % 360) - 180;
        double dalt = m.Alt - pitch;
        var dir = new SKPoint((float)daz, (float)-dalt);
        float len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 1e-3) return;
        dir = new SKPoint(dir.X / len, dir.Y / len);
        float cx = W / 2f, cy = H / 2f, r = Math.Min(W, H) * 0.38f;
        float ax = cx + dir.X * r, ay = cy + dir.Y * r;
        using var ap = new SKPaint { Color = new SKColor(255, 240, 150), IsAntialias = true, Style = SKPaintStyle.Fill };
        float a = MathF.Atan2(dir.Y, dir.X);
        var tip = new SKPoint(ax + 22 * MathF.Cos(a), ay + 22 * MathF.Sin(a));
        var l = new SKPoint(ax + 16 * MathF.Cos(a + 2.5f), ay + 16 * MathF.Sin(a + 2.5f));
        var rr = new SKPoint(ax + 16 * MathF.Cos(a - 2.5f), ay + 16 * MathF.Sin(a - 2.5f));
        using var tri = new SKPath();
        tri.MoveTo(tip); tri.LineTo(l); tri.LineTo(rr); tri.Close();
        c.DrawPath(tri, ap);
        using var txt = new SKPaint { Color = new SKColor(255, 240, 150), IsAntialias = true };
        using var font = new SKFont { Size = 26 };
        c.DrawText("Měsíc", ax, ay - 26, SKTextAlign.Center, font, txt);
    }
}
