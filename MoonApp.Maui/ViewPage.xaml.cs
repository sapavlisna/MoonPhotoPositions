using MoonApp.Core;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace MoonApp.Maui;

/// <summary>
/// Pohled ze stanoviště v panoramatické projekci (azimut vodorovně, elevace svisle) — obdoba
/// 3D náhledu na webu. Kreslí siluetu terénu obarvenou podle vzdálenosti, dráhu tělesa, jeho
/// kotouč ve skutečné úhlové velikosti a značku objektu.
///
/// Ovládání dotykem: tažení = rozhled, dva prsty = přiblížení. Bez přiblížení nemá scéna
/// smysl — právě v něm se pozná, jestli těleso vyjde nad siluetu.
/// </summary>
public partial class ViewPage : ContentPage
{
    readonly double _obsLat, _obsLon, _objLat, _objLon, _objTop, _bearing, _elTarget;
    readonly Body _body;
    readonly IReadOnlyList<MoonSample> _track;
    readonly SkylineSample[] _sky;

    double _centerAz;              // střed pohledu [°]
    double _fovDeg = 60;           // vodorovné zorné pole
    double _panAz0;                // střed na začátku tažení
    double _pinchFov0;
    int _t;                        // index vzorku dráhy
    IDispatcherTimer? _play;

    // dolní mez velikosti tělesa: pod ní by z půl stupně zbyl na displeji bod a scéna by
    // nešla přečíst; nad ní platí skutečný úhlový průměr
    const float BodyMinPx = 14;

    public ViewPage(double obsLat, double obsLon, double objLat, double objLon, double objTop,
        double bearing, double elTarget, Body body, IReadOnlyList<MoonSample> track,
        SkylineSample[] sky, int startIdx)
    {
        InitializeComponent();
        _obsLat = obsLat; _obsLon = obsLon; _objLat = objLat; _objLon = objLon; _objTop = objTop;
        _bearing = bearing; _elTarget = elTarget; _body = body; _track = track; _sky = sky;
        _centerAz = bearing;
        _t = Math.Clamp(startIdx, 0, Math.Max(0, track.Count - 1));

        Canvas.PaintSurface += OnPaint;
        TimeSlider.Maximum = Math.Max(0, track.Count - 1);
        TimeSlider.Value = _t;
        TimeSlider.IsEnabled = track.Count > 0;
        PlayBtn.IsEnabled = track.Count > 0;
        UpdateInfo();
    }

    // ---------- ovládání ----------

    void OnPan(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started) _panAz0 = _centerAz;
        if (e.StatusType is GestureStatus.Running or GestureStatus.Completed)
        {
            // tažení „bere obraz s sebou" jako fotku, ne joystickem
            double w = Math.Max(1, Canvas.Width);
            _centerAz = Norm(_panAz0 - e.TotalX / w * _fovDeg);
            Canvas.InvalidateSurface();
        }
    }

    void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started) _pinchFov0 = _fovDeg;
        else if (e.Status == GestureStatus.Running)
        {
            _fovDeg = Math.Clamp(_pinchFov0 / Math.Max(0.05, e.Scale), 1.5, 170);
            Canvas.InvalidateSurface();
        }
    }

    void OnAimObject(object? sender, EventArgs e)
    {
        _centerAz = _bearing;
        Canvas.InvalidateSurface();
    }

    void OnTimeChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_track.Count == 0) return;
        _t = Math.Clamp((int)Math.Round(e.NewValue), 0, _track.Count - 1);
        UpdateInfo();
        Canvas.InvalidateSurface();
    }

    void OnPlay(object? sender, EventArgs e)
    {
        if (_play is not null) { _play.Stop(); _play = null; PlayBtn.Text = "▶"; return; }
        _play = Dispatcher.CreateTimer();
        _play.Interval = TimeSpan.FromMilliseconds(120);
        _play.Tick += (_, _) =>
        {
            if (_track.Count == 0) return;
            TimeSlider.Value = (_t + 1) % _track.Count;
        };
        _play.Start();
        PlayBtn.Text = "⏸";
    }

    void OnClose(object? sender, EventArgs e)
    {
        _play?.Stop();
        _ = Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed() { OnClose(null, EventArgs.Empty); return true; }

    void UpdateInfo()
    {
        if (_track.Count == 0) { InfoLbl.Text = $"potřebná výška {_elTarget:0.0}°"; return; }
        var s = _track[_t];
        var local = TimeZoneInfo.ConvertTimeFromUtc(s.TimeUtc, Time.Prague);
        TimeLbl.Text = local.ToString("HH:mm");
        double he = Panorama.ElAt(_sky, s.Az);
        string state = s.Alt <= 0 ? "pod obzorem" : s.Alt > he ? "nad obzorem" : "za překážkou";
        InfoLbl.Text = $"az {s.Az:0.0}° · alt {s.Alt:0.0}° · {state} · potř. {_elTarget:0.0}°";
    }

    static double Norm(double az) => (az % 360 + 360) % 360;
    /// <summary>Odchylka azimutu od středu pohledu v rozsahu ±180°.</summary>
    double Rel(double az) => ((az - _centerAz + 540) % 360) - 180;

    // ---------- kresba ----------

    void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        int w = e.Info.Width, h = e.Info.Height;
        c.Clear(new SKColor(0x9f, 0xb8, 0xd8));

        // projekce je isotropní jako na webu: stupeň má stejný počet pixelů vodorovně i svisle
        float pxPerDeg = (float)(w / _fovDeg);
        float vFov = h / pxPerDeg;
        // pohled mířený tak, aby silueta u objektu zůstala v dolní třetině
        double centerEl = _elTarget;
        float X(double az) => (float)(w / 2.0 + Rel(az) * pxPerDeg);
        float Y(double el) => (float)(h / 2.0 - (el - centerEl) * pxPerDeg);

        DrawTerrain(c, w, h, pxPerDeg, X, Y);
        DrawTrack(c, X, Y);
        DrawObject(c, X, Y);
        DrawBody(c, pxPerDeg, X, Y);
        DrawScale(c, w, h, vFov);
    }

    void DrawTerrain(SKCanvas c, int w, int h, float pxPerDeg, Func<double, float> X, Func<double, float> Y)
    {
        if (_sky.Length == 0) return;
        double half = _fovDeg / 2 + 1;
        double maxDist = Math.Max(1, _sky.Max(s => s.DistM));

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        // po svislých pruzích, aby šla každá část obarvit podle vlastní vzdálenosti — barva
        // nese hloubku scény stejně jako na webu (blízko zelená, daleko do oranžova)
        for (int i = 0; i < _sky.Length; i++)
        {
            double rel = Rel(_sky[i].Az);
            if (Math.Abs(rel) > half) continue;
            float x0 = X(_sky[i].Az), y0 = Y(_sky[i].El);
            float x1 = i + 1 < _sky.Length ? X(_sky[i + 1].Az) : x0 + 1;
            if (x1 < x0) x1 = x0 + 1;
            fill.Color = DepthColor(SmoothDist(i) / maxDist);
            c.DrawRect(x0, y0, Math.Max(1, x1 - x0) + 1, h - y0, fill);
        }
    }

    /// <summary>
    /// Vzdálenost průměrovaná přes okolní azimuty. Sousední sloupce se v syrových datech liší
    /// o stovky metrů (jednou keř před nosem, hned vedle vzdálený kopec) a barva by skákala
    /// v pruzích místo aby nesla hloubku krajiny.
    /// </summary>
    double SmoothDist(int i)
    {
        const int W = 6;
        double sum = 0; int n = 0;
        for (int j = i - W; j <= i + W; j++)
        {
            if (j < 0 || j >= _sky.Length) continue;
            sum += _sky[j].DistM; n++;
        }
        return n > 0 ? sum / n : _sky[i].DistM;
    }

    /// <summary>Blízko zelená, daleko do oranžova — atmosférická perspektiva jako na webu.</summary>
    static SKColor DepthColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        SKColor near = new(0x33, 0x60, 0x1f), mid = new(0x9c, 0xae, 0x52), far = new(0xcc, 0x6a, 0x35);
        return t < 0.5 ? Lerp(near, mid, t * 2) : Lerp(mid, far, t * 2 - 1);
    }

    static SKColor Lerp(SKColor a, SKColor b, double t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t));

    void DrawTrack(SKCanvas c, Func<double, float> X, Func<double, float> Y)
    {
        if (_track.Count < 2) return;
        using var p = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            Color = _body == Body.Sun ? new SKColor(0xff, 0xd2, 0x1e) : new SKColor(0xff, 0x9a, 0x1f),
        };
        double half = _fovDeg / 2 + 2;
        SKPoint? prev = null;
        foreach (var s in _track)
        {
            if (Math.Abs(Rel(s.Az)) > half) { prev = null; continue; }
            var pt = new SKPoint(X(s.Az), Y(s.Alt));
            if (prev is { } q) c.DrawLine(q, pt, p);
            prev = pt;
        }
    }

    void DrawObject(SKCanvas c, Func<double, float> X, Func<double, float> Y)
    {
        if (Math.Abs(Rel(_bearing)) > _fovDeg / 2 + 1) return;
        float x = X(_bearing), y = Y(_elTarget);
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(0xff, 0x2f, 0x55) };
        c.DrawCircle(x, y, 7, p);
        p.Style = SKPaintStyle.Stroke;
        p.StrokeWidth = 3;
        c.DrawLine(x, y, x, y + 26, p);
    }

    void DrawBody(SKCanvas c, float pxPerDeg, Func<double, float> X, Func<double, float> Y)
    {
        if (_track.Count == 0) return;
        var s = _track[_t];
        if (s.Alt <= -5 || Math.Abs(Rel(s.Az)) > _fovDeg / 2 + 1) return;
        float r = Math.Max(BodyMinPx / 2, (float)Panorama.BodyRadiusDeg(_body) * pxPerDeg);
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = _body == Body.Sun ? new SKColor(0xff, 0xf0, 0xa8) : new SKColor(0xf3, 0xf0, 0xe6),
        };
        c.DrawCircle(X(s.Az), Y(s.Alt), r, p);
    }

    void DrawScale(SKCanvas c, int w, int h, float vFov)
    {
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 140) };
        using var f = new SKFont { Size = 26 };
        string txt = $"{_centerAz:0}° · záběr {_fovDeg:0.#}°";
        c.DrawRect(0, 0, w, 40, p);
        p.Color = SKColors.White;
        c.DrawText(txt, 12, 29, SKTextAlign.Left, f, p);
    }
}
