namespace MoonApp.Core;

/// <summary>Výsledná mřížka pokrytí (řádek 0 = sever, pro přímý overlay).</summary>
public sealed class CoverageGrid
{
    public double N, S, E, W;
    public int NRows, NCols, Max, Visible, Aligned, CellsTested;
    public int[] Scores = [];   // row-major, délka NRows*NCols
    public double TopZ, BaseZ;
}

/// <summary>
/// Coverage heatmapa: odkud v okruhu projde Měsíc nad objektem a objekt je odtud vidět (LOS).
/// Port src/pipeline.py:coverage. Vše na zařízení (ČÚZK rastry + CoordinateSharp dráha).
/// </summary>
public static class Coverage
{
    public static async Task<CoverageGrid> ComputeAsync(double slat, double slon, DateOnly date, int days,
        double radiusM, double resM, double eyeH, double subjectH,
        double azTol = 2, double altBand = 2, double stepMin = 5, double dMin = 80,
        bool visOnly = false, string? cacheDir = null, IProgress<ProgressInfo>? progress = null)
    {
        progress?.Report(new("Stahuji výškopis povrchu (ČÚZK)…"));
        var dmp = await Cuzk.LoadAroundAsync(slat, slon, radiusM + 200, 5.0, Cuzk.Dmp, cacheDir);
        progress?.Report(new("Stahuji výškopis terénu (ČÚZK)…"));
        var dmr = await Cuzk.LoadAroundAsync(slat, slon, radiusM + 200, 5.0, Cuzk.Dmr5g, cacheDir);

        var (ox, oy) = Geo.ToSjtsk(slon, slat);
        double baseZ = dmr.Sample(ox, oy);
        double topDmp = double.NegativeInfinity;
        for (double gy = -12; gy <= 12; gy += 2)
            for (double gx = -12; gx <= 12; gx += 2)
            {
                double v = dmp.Sample(ox + gx, oy + gy);
                if (!double.IsNaN(v) && v > topDmp) topDmp = v;
            }
        double zSubj = baseZ + Math.Max(topDmp - baseZ, subjectH);

        // dráha Měsíce v lokálním okně [date 00:00, +days) → UTC
        progress?.Report(new("Počítám dráhu Měsíce…"));
        var localStart = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, Time.Prague);
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(days), Time.Prague);
        var track = Astro.Track(slat, slon, utcStart, utcEnd, stepMin);

        // mřížka lat/lon
        progress?.Report(new("Sestavuji mřížku okolí…"));
        double cosLat = Math.Cos(slat * Math.PI / 180.0);
        double hlat = radiusM / 111320.0, hlon = radiusM / (111320.0 * cosLat);
        double dlat = resM / 111320.0, dlon = resM / (111320.0 * cosLat);
        var lats = Arange(slat - hlat, slat + hlat, dlat);
        var lons = Arange(slon - hlon, slon + hlon, dlon);
        int nrows = lats.Length, ncols = lons.Length, ncells = nrows * ncols;

        var elReq = new double[ncells];
        var azCs = new double[ncells];
        var cx = new double[ncells];
        var cy = new double[ncells];
        var eyeZ = new double[ncells];
        for (int r = 0; r < nrows; r++)
            for (int c = 0; c < ncols; c++)
            {
                int j = r * ncols + c; double la = lats[r], lo = lons[c];
                double dist = Geo.Distance(la, lo, slat, slon);
                azCs[j] = Geo.Bearing(la, lo, slat, slon);
                var (x, y) = Geo.ToSjtsk(lo, la); cx[j] = x; cy[j] = y;
                double ez = dmr.Sample(x, y) + eyeH; eyeZ[j] = ez;
                elReq[j] = (dist >= dMin && dist <= radiusM)
                    ? Math.Atan2(zSubj - ez - Raycast.Drop(dist), dist) * 180.0 / Math.PI
                    : double.NaN;
            }

        var opp = new int[ncells];
        if (!visOnly)
        {
            progress?.Report(new("Hledám zarovnání s Měsícem…", 0));
            int nt = track.Count;
            for (int ti = 0; ti < nt; ti++)
            {
                var s = track[ti];
                if (s.Alt > 0)
                {
                    double am = s.Alt, azm = s.Az;
                    for (int j = 0; j < ncells; j++)
                    {
                        double er = elReq[j];
                        if (double.IsNaN(er)) continue;
                        double daz = Math.Abs(((azCs[j] - azm + 180) % 360 + 360) % 360 - 180);
                        if (daz <= azTol && Math.Abs(er - am) <= altBand) opp[j]++;
                    }
                }
                if (ti % 8 == 0 || ti == nt - 1)
                    progress?.Report(new("Hledám zarovnání s Měsícem…", (double)(ti + 1) / nt));
            }
        }

        var aligned = new List<int>();
        for (int j = 0; j < ncells; j++)
            if (visOnly ? !double.IsNaN(elReq[j]) : opp[j] > 0) aligned.Add(j);

        var ax = new double[aligned.Count];
        var ay = new double[aligned.Count];
        var aez = new double[aligned.Count];
        for (int k = 0; k < aligned.Count; k++) { int j = aligned[k]; ax[k] = cx[j]; ay[k] = cy[j]; aez[k] = eyeZ[j]; }
        progress?.Report(new("Kontrola viditelnosti (LOS)…", 0));
        var vis = Raycast.LosClearBatch(dmp, ax, ay, aez, ox, oy, zSubj,
            onProgress: f => progress?.Report(new("Kontrola viditelnosti (LOS)…", f)));

        var score = new int[ncells];
        for (int k = 0; k < aligned.Count; k++)
            if (vis[k]) score[aligned[k]] = visOnly ? 1 : opp[aligned[k]];

        // flipud → řádek 0 = sever
        var scores = new int[ncells]; int max = 0, visible = 0;
        for (int r = 0; r < nrows; r++)
            for (int c = 0; c < ncols; c++)
            {
                int src = r * ncols + c, dst = (nrows - 1 - r) * ncols + c;
                scores[dst] = score[src];
                if (score[src] > 0) { visible++; if (score[src] > max) max = score[src]; }
            }

        return new CoverageGrid
        {
            N = lats[^1], S = lats[0], E = lons[^1], W = lons[0],
            NRows = nrows, NCols = ncols, Max = max, Scores = scores,
            Visible = visible, Aligned = aligned.Count, CellsTested = ncells,
            TopZ = zSubj, BaseZ = baseZ,
        };
    }

    static double[] Arange(double a, double b, double step)
    {
        var l = new List<double>();
        for (double v = a; v <= b + 1e-12; v += step) l.Add(v);
        return [.. l];
    }
}
