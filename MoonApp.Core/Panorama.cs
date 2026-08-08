namespace MoonApp.Core;

/// <summary>Silueta v jednom azimutu: elevace nejvyšší překážky a jak je daleko.</summary>
public readonly record struct SkylineSample(double Az, double El, double DistM);

/// <summary>
/// Pohled ze stanoviště. Web ho kreslí 3D meshem, ale zobrazuje panoramatickou projekci —
/// azimut vodorovně, elevace svisle. To je 2D úloha: pro každý sloupec obrazu stačí elevace
/// nejvyšší překážky a její vzdálenost (z ní se bere barva). Žádný renderer tedy netřeba,
/// jen o něco bohatší profil, než umí <see cref="Raycast.HorizonProfile"/>.
/// </summary>
public static class Panorama
{
    /// <summary>
    /// Silueta v rozsahu azimutů. rMax je dohled; dr krok podél paprsku. Vrací vzorky
    /// po azStep stupních — a u každého i vzdálenost překážky, aby šla krajina obarvit
    /// podle hloubky jako na webu.
    /// </summary>
    public static SkylineSample[] Skyline(Dsm dmp, double lat, double lon,
        double eyeH = 1.7, double rMin = 20, double rMax = 4000, double dr = 10,
        double azFrom = 0, double azTo = 360, double azStep = 0.25,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var (cx, cy) = Geo.ToSjtsk(lon, lat);
        double eyeZ = dmp.Sample(cx, cy) + eyeH;
        if (double.IsNaN(eyeZ))
            throw new InvalidOperationException("Stanoviště je mimo výškopisná data ČÚZK.");

        int n = Math.Max(1, (int)Math.Round((azTo - azFrom) / azStep));
        var prof = new SkylineSample[n];
        int done = 0;
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
        {
            double az = azFrom + i * azStep, maxEl = -90, atDist = rMax;
            for (double r = rMin; r <= rMax; r += dr)
            {
                var (plat, plon) = Geo.Destination(lat, lon, az, r);
                var (x, y) = Geo.ToSjtsk(plon, plat);
                double z = dmp.Sample(x, y);
                if (double.IsNaN(z)) continue;
                double el = Math.Atan2(z - eyeZ - Raycast.Drop(r), r) * 180.0 / Math.PI;
                if (el > maxEl) { maxEl = el; atDist = r; }
            }
            prof[i] = new SkylineSample(az, maxEl, atDist);
            int c = Interlocked.Increment(ref done);
            if (c % 64 == 0 || c == n)
                progress?.Report(new("Počítám panorama…", (double)c / n));
        });
        return prof;
    }

    /// <summary>
    /// Silueta rozložená do vzdálenostních pásem: pro každé pásmo elevace nejvyšší překážky
    /// v něm. Kreslí se odzadu dopředu a krajina tím dostane hloubku — jedna silueta ukáže
    /// jen nejvyšší hranu, takže z kopcovité krajiny zbude plochý pás.
    /// </summary>
    /// <returns>bands[b][i] = elevace v pásmu b a azimutu i; Edges[b] = horní mez pásma [m].</returns>
    public static (double[][] Bands, double[] Edges, double[] Az) Layers(Dsm dmp, double lat, double lon,
        double eyeH = 1.7, double rMin = 20, double rMax = 4000, double dr = 10,
        double azFrom = 0, double azTo = 360, double azStep = 0.5, int bands = 5,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var (cx, cy) = Geo.ToSjtsk(lon, lat);
        double eyeZ = dmp.Sample(cx, cy) + eyeH;
        if (double.IsNaN(eyeZ))
            throw new InvalidOperationException("Stanoviště je mimo výškopisná data ČÚZK.");

        // pásma rostou geometricky: blízko je na stejný kus krajiny potřeba víc detailu
        var edges = new double[bands];
        for (int b = 0; b < bands; b++)
            edges[b] = rMin * Math.Pow(rMax / rMin, (b + 1.0) / bands);

        int n = Math.Max(1, (int)Math.Round((azTo - azFrom) / azStep));
        var az = new double[n];
        var res = new double[bands][];
        for (int b = 0; b < bands; b++) { res[b] = new double[n]; Array.Fill(res[b], -90.0); }

        int done = 0;
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
        {
            double a = azFrom + i * azStep;
            az[i] = a;
            for (double r = rMin; r <= rMax; r += dr)
            {
                var (plat, plon) = Geo.Destination(lat, lon, a, r);
                var (x, y) = Geo.ToSjtsk(plon, plat);
                double z = dmp.Sample(x, y);
                if (double.IsNaN(z)) continue;
                double el = Math.Atan2(z - eyeZ - Raycast.Drop(r), r) * 180.0 / Math.PI;
                int b = 0;
                while (b < bands - 1 && r > edges[b]) b++;
                if (el > res[b][i]) res[b][i] = el;
            }
            int c = Interlocked.Increment(ref done);
            if (c % 64 == 0 || c == n) progress?.Report(new("Počítám panorama…", (double)c / n));
        });
        return (res, edges, az);
    }

    /// <summary>Elevace siluety v daném azimutu (lineárně mezi vzorky).</summary>
    public static double ElAt(SkylineSample[] prof, double az)
    {
        if (prof.Length == 0) return -90;
        double step = prof.Length > 1 ? prof[1].Az - prof[0].Az : 1;
        double f = (az - prof[0].Az) / step;
        int i = (int)Math.Floor(f);
        if (i < 0 || i >= prof.Length - 1) return prof[Math.Clamp(i, 0, prof.Length - 1)].El;
        double t = f - i;
        return prof[i].El * (1 - t) + prof[i + 1].El * t;
    }

    /// <summary>Vzdálenost siluety v daném azimutu — z ní se odvozuje barva krajiny.</summary>
    public static double DistAt(SkylineSample[] prof, double az)
    {
        if (prof.Length == 0) return 0;
        double step = prof.Length > 1 ? prof[1].Az - prof[0].Az : 1;
        int i = (int)Math.Round((az - prof[0].Az) / step);
        return prof[Math.Clamp(i, 0, prof.Length - 1)].DistM;
    }

    /// <summary>
    /// Zjemnění siluety ve směru na objekt z metrového rastru staženého kolem něj. Odpovídá
    /// koridoru na webu: hrubý rastr věž nebo hřeben zprůměruje a právě o jejich hranu jde,
    /// když se rozhoduje, jestli těleso vyjde nad ně, nebo za ně.
    /// </summary>
    public static async Task<SkylineSample[]> RefineTowardAsync(double obsLat, double obsLon,
        double objLat, double objLon, double eyeH, double halfWidthDeg = 3, double azStep = 0.05,
        string? cacheDir = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        double dist = Geo.Distance(obsLat, obsLon, objLat, objLon);
        double bearing = Geo.Bearing(obsLat, obsLon, objLat, objLon);
        // rastr kolem objektu musí pokrýt i kus krajiny okolo, ať je vidět, do čeho zapadá
        double patchR = Math.Clamp(dist * 0.12, 150, 600);
        progress?.Report(new("Stahuji jemný výškopis objektu (ČÚZK)…"));
        var fine = await Cuzk.LoadAroundAsync(objLat, objLon, patchR, 1.0, Cuzk.Dmp, cacheDir);

        // výška oka se bere z hrubého rastru u stanoviště — jemný ho nepokrývá
        var coarse = await Cuzk.LoadAroundAsync(obsLat, obsLon, 300, 5.0, Cuzk.Dmp, cacheDir);
        var (ox, oy) = Geo.ToSjtsk(obsLon, obsLat);
        double eyeZ = coarse.Sample(ox, oy) + eyeH;

        int n = Math.Max(1, (int)Math.Round(2 * halfWidthDeg / azStep));
        var res = new SkylineSample[n];
        double rFrom = Math.Max(50, dist - patchR), rTo = dist + patchR;
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
        {
            double a = bearing - halfWidthDeg + i * azStep, maxEl = -90, at = dist;
            var (ox2, oy2) = Geo.ToSjtsk(objLon, objLat);
            for (double r = rFrom; r <= rTo; r += 1.0)
            {
                var (plat, plon) = Geo.Destination(obsLat, obsLon, a, r);
                var (x, y) = Geo.ToSjtsk(plon, plat);
                // vzorkovat jen uvnitř staženého výřezu: za jeho okrajem vrací rastr krajní
                // hodnotu, ne NaN, a zjemnění by se rozlilo do bloku přes celý pás
                if ((x - ox2) * (x - ox2) + (y - oy2) * (y - oy2) > patchR * patchR) continue;
                double z = fine.Sample(x, y);
                if (double.IsNaN(z)) continue;
                double el = Math.Atan2(z - eyeZ - Raycast.Drop(r), r) * 180.0 / Math.PI;
                if (el > maxEl) { maxEl = el; at = r; }
            }
            res[i] = new SkylineSample(a, maxEl, at);
        });
        progress?.Report(new("Hotovo", 1));
        return res;
    }

    /// <summary>
    /// Zdánlivý poloměr tělesa [°]. Měsíc i Slunce mají shodně kolem půl stupně — v přiblížení
    /// se podle toho posuzuje, jestli se vejde nad siluetu, takže se to nesmí kreslit „nějak“.
    /// </summary>
    public static double BodyRadiusDeg(Body body) => body == Body.Sun ? 0.533 / 2 : 0.518 / 2;
}
