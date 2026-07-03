namespace MoonApp.Core;

public readonly record struct SnapResult(
    double Lat, double Lon, double Top, double Base, double Height, double Point, double Avg);

public readonly record struct AreaResult(double? Elev, double? Avg50);

/// <summary>
/// Terénní geometrie nad rastrem (EPSG:5514). Port src/raycast.py + pipeline._los_clear_batch.
/// Pravé azimuty/destinace sféricky (Geo), vzorkování v 5514 (Dsm).
/// </summary>
public static class Raycast
{
    const double RefractionK = 0.13;

    /// <summary>Pokles obzoru zakřivením + refrakcí [m] na vzdálenosti r [m].</summary>
    public static double Drop(double r) => (1 - RefractionK) * r * r / (2 * Geo.EarthRadiusM);

    static double ElevDeg(double zTarget, double zEye, double r)
        => Math.Atan2(zTarget - zEye - Drop(r), r) * 180.0 / Math.PI;

    static double MeanValid(float[] data)
    {
        double sum = 0; int n = 0;
        foreach (var v in data) if (Math.Abs(v) <= 1e5) { sum += v; n++; }
        return n > 0 ? sum / n : double.NaN;
    }

    /// <summary>Přichycení na nejvyšší bod (DMP) v okolí + odhad výšky objektu a statistiky okolí.</summary>
    public static async Task<SnapResult> SnapPeakAsync(double lat, double lon,
        double radiusM = 50, double cellM = 1.0, string? cacheDir = null,
        IProgress<ProgressInfo>? progress = null)
    {
        progress?.Report(new("Stahuji výškopis povrchu (ČÚZK)…"));
        var dmp = await Cuzk.LoadAroundAsync(lat, lon, radiusM, cellM, Cuzk.Dmp, cacheDir);
        progress?.Report(new("Hledám nejvyšší bod objektu…"));
        var (col, row, top) = dmp.ArgMax();
        var (px, py) = dmp.PixelCenter(col, row);
        var (plon, plat) = Geo.ToWgs84(px, py);
        progress?.Report(new("Zjišťuji výšku terénu (ČÚZK)…"));
        double? baseZ = await Cuzk.IdentifyElevAsync(plat, plon, Cuzk.Dmr5g);
        double baseV = baseZ ?? MeanValid(dmp.Data);
        var (cx, cy) = Geo.ToSjtsk(lon, lat);
        double point = dmp.Sample(cx, cy);
        double avg = MeanValid(dmp.Data);
        return new SnapResult(plat, plon, top, baseV, top - baseV, point, avg);
    }

    /// <summary>Výška v bodě a průměr v kruhovém okolí radiusM (z daného rastru).</summary>
    public static AreaResult AreaStats(Dsm dsm, double lat, double lon, double radiusM = 50, double stepM = 5)
    {
        var (cx, cy) = Geo.ToSjtsk(lon, lat);
        double point = dsm.Sample(cx, cy);
        double sum = 0; int n = 0;
        for (double gy = -radiusM; gy <= radiusM + 1e-6; gy += stepM)
            for (double gx = -radiusM; gx <= radiusM + 1e-6; gx += stepM)
                if (gx * gx + gy * gy <= radiusM * radiusM)
                {
                    double v = dsm.Sample(cx + gx, cy + gy);
                    if (!double.IsNaN(v)) { sum += v; n++; }
                }
        return new AreaResult(double.IsNaN(point) ? null : point, n > 0 ? sum / n : null);
    }

    /// <summary>Silueta obzoru: pro každý azimut (krok azStep) max. elevační úhel překážky.</summary>
    public static (double Az, double El)[] HorizonProfile(Dsm dmp, double lat, double lon,
        double eyeH = 1.7, double rMin = 10, double rMax = 1500, double dr = 5, double azStep = 1.0,
        Action<double>? onProgress = null)
    {
        var (cx, cy) = Geo.ToSjtsk(lon, lat);
        double eyeZ = dmp.Sample(cx, cy) + eyeH;
        int n = (int)Math.Round(360.0 / azStep);
        var prof = new (double, double)[n];
        for (int i = 0; i < n; i++)
        {
            double az = i * azStep, maxEl = -90;
            for (double r = rMin; r <= rMax; r += dr)
            {
                var (plat, plon) = Geo.Destination(lat, lon, az, r);
                var (x, y) = Geo.ToSjtsk(plon, plat);
                double z = dmp.Sample(x, y);
                if (double.IsNaN(z)) continue;
                double el = ElevDeg(z, eyeZ, r);
                if (el > maxEl) maxEl = el;
            }
            prof[i] = (az, maxEl);
            if (onProgress != null && (i % 8 == 0 || i == n - 1)) onProgress((double)(i + 1) / n);
        }
        return prof;
    }

    /// <summary>
    /// Vektorové LOS pro mnoho buněk (vše v EPSG:5514, lineární interpolace paprsku buňka→objekt).
    /// True = vrchol objektu je odtud vidět. Port pipeline._los_clear_batch. Paralelně přes buňky.
    /// </summary>
    public static bool[] LosClearBatch(Dsm dmp, double[] cx, double[] cy, double[] eyeZ,
        double sx, double sy, double zSubj, int nsteps = 384, double footprintM = 15,
        Action<double>? onProgress = null)
    {
        var outv = new bool[cx.Length];
        int total = cx.Length, done = 0;
        int reportEvery = Math.Max(1, total / 50);   // ~2% kroky
        Parallel.For(0, cx.Length, i =>
        {
            double dx = sx - cx[i], dy = sy - cy[i];
            double dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1.0);
            double elTarget = ElevDeg(zSubj, eyeZ[i], dist);
            double tmax = Math.Clamp(1 - footprintM / dist, 0, 1);
            double t0 = Math.Min(8.0 / dist, tmax);
            double maxEl = -90;
            for (int s = 0; s < nsteps; s++)
            {
                double tt = t0 + (tmax - t0) * s / (nsteps - 1);
                double r = dist * tt;
                double z = dmp.Sample(cx[i] + dx * tt, cy[i] + dy * tt);
                if (double.IsNaN(z)) continue;
                double el = ElevDeg(z, eyeZ[i], r);
                if (el > maxEl) maxEl = el;
            }
            outv[i] = maxEl <= elTarget + 1e-6;
            if (onProgress != null)
            {
                int d = Interlocked.Increment(ref done);
                if (d % reportEvery == 0 || d == total) onProgress((double)d / total);
            }
        });
        return outv;
    }
}
