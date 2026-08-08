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
    /// Zdánlivý poloměr tělesa [°]. Měsíc i Slunce mají shodně kolem půl stupně — v přiblížení
    /// se podle toho posuzuje, jestli se vejde nad siluetu, takže se to nesmí kreslit „nějak“.
    /// </summary>
    public static double BodyRadiusDeg(Body body) => body == Body.Sun ? 0.533 / 2 : 0.518 / 2;
}
