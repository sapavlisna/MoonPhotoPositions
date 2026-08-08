namespace MoonApp.Core;

/// <summary>Den, kdy těleso projde nad objektem: kdy přesně, v jaké výšce a jak přesně v ose.</summary>
public readonly record struct WindowDay(DateOnly Date, DateTime BestUtc, double Alt, double AzErrDeg);

/// <summary>
/// „Kdy odsud vidět“ — pro dané stanoviště a objekt najde dny v rozmezí, kdy těleso projde
/// nad vrcholem objektu. Obdoba /window z Python backendu, ale jen pro jeden bod: mřížku
/// přes celé okolí × všechny dny by telefon počítal hodiny, kdežto jeden bod jsou vteřiny.
///
/// Terén se čte jednou — potřebná výška ani směr na objekt na datu nezávisí, mění se jen dráha.
/// </summary>
public static class SkyWindow
{
    public static async Task<List<WindowDay>> ForPointAsync(
        double objLat, double objLon, double objTopZ,
        double obsLat, double obsLon, DateOnly from, DateOnly to,
        // 10 min stačí: hledá se, *který den* to vyjde, ne přesná vteřina — a přes stovky dní
        // je každý vzorek navíc znát. Přesný čas dá pak plánovač pro vybraný den.
        Body body = Body.Moon, double eyeH = 1.7, double azTol = 2, double altBand = 2,
        double stepMin = 10, string? cacheDir = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new("Stahuji výškopis povrchu (ČÚZK)…"));
        double dist = Geo.Distance(obsLat, obsLon, objLat, objLon);
        double bearing = Geo.Bearing(obsLat, obsLon, objLat, objLon);
        var dmp = await Cuzk.LoadAroundAsync(obsLat, obsLon, 300, 5.0, Cuzk.Dmp, cacheDir);
        var (ox, oy) = Geo.ToSjtsk(obsLon, obsLat);
        double eyeZ = dmp.Sample(ox, oy) + eyeH;
        // NaN se v porovnáních chová tiše (každé je false), takže by výsledkem byl prázdný
        // seznam bez vysvětlení — mimo výškopisná data se to musí říct nahlas
        if (double.IsNaN(eyeZ))
            throw new InvalidOperationException("Stanoviště je mimo výškopisná data ČÚZK.");
        double elTarget = Math.Atan2(objTopZ - eyeZ - Raycast.Drop(dist), dist) * 180.0 / Math.PI;

        if (body == Body.Vis) return [];        // bez tělesa není co hledat

        int total = to.DayNumber - from.DayNumber + 1;
        // dny jsou na sobě nezávislé, takže se rozdělí na jádra — na telefonu je to rozdíl
        // mezi „chvilkou“ a „než to dopočítá, tak to zavřu“
        var found = new WindowDay?[total];
        int done = 0;
        Parallel.For(0, total, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var date = from.AddDays(i);
            int n = Interlocked.Increment(ref done);
            if (n % 5 == 0 || n == total)
                progress?.Report(new($"Procházím dny… {n}/{total}", (double)n / total));

            var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
            var utcStart = TimeZoneInfo.ConvertTimeToUtc(local, Time.Prague);

            // Dvě fáze: hrubým krokem se najdou úseky, kudy těleso vůbec chodí kolem objektu,
            // a jen ty se projdou jemně. Astronomický výpočet je nejdražší část celého okna,
            // takže hrubý předvýběr rozhoduje o tom, jestli je to na telefonu použitelné.
            // Rezervy kryjí, co těleso stihne za hrubý krok ujet (azimut ~0,25°/min, výška ~0,2°/min).
            const double CoarseMin = 30;
            double azSlack = azTol + 0.25 * CoarseMin, altSlack = altBand + 0.2 * CoarseMin;

            DateTime? best = null; double bestErr = double.MaxValue, bestAlt = 0, bestAz = 0;
            void Refine(DateTime from2, DateTime to2)
            {
                foreach (var s in Astro.Track(body, obsLat, obsLon, from2, to2, stepMin))
                {
                    if (s.Alt <= 0) continue;
                    double daz = Math.Abs(((s.Az - bearing + 180) % 360 + 360) % 360 - 180);
                    if (daz > azTol || Math.Abs(s.Alt - elTarget) > altBand) continue;
                    double err = Math.Abs(s.Alt - elTarget) + daz;
                    if (err < bestErr) { bestErr = err; best = s.TimeUtc; bestAlt = s.Alt; bestAz = daz; }
                }
            }

            foreach (var c in Astro.Track(body, obsLat, obsLon, utcStart, utcStart.AddDays(1), CoarseMin))
            {
                if (c.Alt <= -altSlack) continue;
                double daz = Math.Abs(((c.Az - bearing + 180) % 360 + 360) % 360 - 180);
                if (daz > azSlack || Math.Abs(c.Alt - elTarget) > altSlack) continue;
                Refine(c.TimeUtc.AddMinutes(-CoarseMin), c.TimeUtc.AddMinutes(CoarseMin));
            }
            if (best is { } t) found[i] = new WindowDay(date, t, bestAlt, bestAz);
        });
        progress?.Report(new("Hotovo", 1));
        return [.. found.Where(d => d.HasValue).Select(d => d!.Value)];
    }
}
