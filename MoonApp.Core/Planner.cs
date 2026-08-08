namespace MoonApp.Core;

/// <summary>Analýza stanoviště: viditelnost objektu, horizont, dráha tělesa, čas „na špici".</summary>
public readonly record struct ViewpointResult(
    bool Clear, double DistanceM, double Bearing, double ElTargetDeg,
    DateTime? OnTipUtc, double? OnTipAlt,
    (double Az, double El)[] Horizon, IReadOnlyList<MoonSample> Track);

public static class Planner
{
    /// <summary>
    /// Z bodu stanoviště spočítá viditelnost vrcholu objektu (LOS), siluetu horizontu, denní
    /// dráhu tělesa a okamžik, kdy těleso „sedí" na vrcholu (az ≈ směr na objekt, alt ≈ potřebná).
    /// </summary>
    public static async Task<ViewpointResult> ViewpointAsync(
        double objLat, double objLon, double objTopZ,
        double obsLat, double obsLon, DateOnly date,
        double eyeH = 1.7, double horizonRMax = 1500, string? cacheDir = null,
        IProgress<ProgressInfo>? progress = null, Body body = Body.Moon)
    {
        double dist = Geo.Distance(obsLat, obsLon, objLat, objLon);
        double bearing = Geo.Bearing(obsLat, obsLon, objLat, objLon);

        double radius = Math.Max(dist + 200, horizonRMax + 100);
        progress?.Report(new("Stahuji výškopis povrchu (ČÚZK)…"));
        var dmp = await Cuzk.LoadAroundAsync(obsLat, obsLon, radius, 5.0, Cuzk.Dmp, cacheDir);
        var (ox, oy) = Geo.ToSjtsk(obsLon, obsLat);
        double eyeZ = dmp.Sample(ox, oy) + eyeH;
        double elTarget = Math.Atan2(objTopZ - eyeZ - Raycast.Drop(dist), dist) * 180.0 / Math.PI;

        progress?.Report(new("Kontroluji viditelnost objektu…"));
        var (sx, sy) = Geo.ToSjtsk(objLon, objLat);
        bool clear = Raycast.LosClearBatch(dmp, [ox], [oy], [eyeZ], sx, sy, objTopZ)[0];

        progress?.Report(new("Počítám siluetu horizontu…", 0));
        var horizon = Raycast.HorizonProfile(dmp, obsLat, obsLon, eyeH, 10, horizonRMax, 5, 2.0,
            onProgress: f => progress?.Report(new("Počítám siluetu horizontu…", f)));

        if (body == Body.Vis)   // režim „jen viditelnost“ — dráha ani špička nedávají smysl
            return new(clear, dist, bearing, elTarget, null, null, horizon, []);

        // Měsíc se fotí přes noc, Slunce přes den — okno musí sedět na těleso, jinak by
        // u Slunce vyšla samá záporná výška
        progress?.Report(new($"Počítám dráhu {Names.Gen(body)}…"));
        int startHour = body == Body.Sun ? 3 : 16;
        var localStart = new DateTime(date.Year, date.Month, date.Day, startHour, 0, 0, DateTimeKind.Unspecified);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, Time.Prague);
        var track = Astro.Track(body, obsLat, obsLon, utcStart, utcStart.AddHours(16), 2);

        progress?.Report(new($"Hledám čas {Names.Gen(body)} na špici…"));
        DateTime? onTip = null; double? onAlt = null; double bestErr = double.MaxValue;
        foreach (var s in track)
        {
            if (s.Alt <= 0) continue;
            double daz = Math.Abs(((s.Az - bearing + 180) % 360 + 360) % 360 - 180);
            if (daz > 3) continue;
            double err = Math.Abs(s.Alt - elTarget);
            if (err < bestErr) { bestErr = err; onTip = s.TimeUtc; onAlt = s.Alt; }
        }
        return new(clear, dist, bearing, elTarget, onTip, onAlt, horizon, track);
    }
}
