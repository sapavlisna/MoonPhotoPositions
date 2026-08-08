using CoordinateSharp;

namespace MoonApp.Core;

/// <summary>Těleso, které se plánuje. Vis = jen viditelnost objektu, bez tělesa.</summary>
public enum Body { Moon, Sun, Vis }

/// <summary>Jeden vzorek dráhy tělesa: čas (UTC), azimut a výška [°].</summary>
public readonly record struct MoonSample(DateTime TimeUtc, double Az, double Alt);

/// <summary>
/// Astronomie na zařízení přes CoordinateSharp (nahrazuje skyfield + de421.bsp z
/// Python backendu, src/astro.py). Vstupy v UTC.
/// </summary>
public static class Astro
{
    // Bez tohohle počítá CoordinateSharp při každém vzorku i UTM, MGRS, ECEF a celý
    // sluneční i měsíční cyklus. Pro dráhu stačí nebeská část — u tisíců vzorků
    // (okno „kdy odsud vidět“ jich má desetitisíce) je to rozdíl mezi vteřinami a minutami.
    static readonly EagerLoad CelestialOnly = new(EagerLoadType.Celestial);

    /// <summary>Azimut [°] a výška [°] tělesa z místa v daný UTC čas.</summary>
    public static MoonSample At(Body body, double lat, double lon, DateTime utc)
    {
        var c = new Coordinate(lat, lon, DateTime.SpecifyKind(utc, DateTimeKind.Utc), CelestialOnly);
        var ci = c.CelestialInfo;
        return body == Body.Sun
            ? new MoonSample(utc, ci.SunAzimuth, ci.SunAltitude)
            : new MoonSample(utc, ci.MoonAzimuth, ci.MoonAltitude);
    }

    /// <summary>Azimut [°] a výška [°] Měsíce z místa v daný UTC čas.</summary>
    public static MoonSample MoonAt(double lat, double lon, DateTime utc) => At(Body.Moon, lat, lon, utc);

    /// <summary>Dráha tělesa po krocích stepMin [min] v intervalu [fromUtc, toUtc].</summary>
    public static List<MoonSample> Track(Body body, double lat, double lon,
        DateTime fromUtc, DateTime toUtc, double stepMin)
    {
        var list = new List<MoonSample>();
        for (var t = fromUtc; t <= toUtc; t = t.AddMinutes(stepMin))
            list.Add(At(body, lat, lon, t));
        return list;
    }

    public static List<MoonSample> Track(double lat, double lon, DateTime fromUtc, DateTime toUtc, double stepMin)
        => Track(Body.Moon, lat, lon, fromUtc, toUtc, stepMin);

    /// <summary>
    /// Fáze Měsíce pro daný den: Fraction = osvětlená část (0..1),
    /// Phase = 0/1 nov, 0.5 úplněk (0–0.5 dorůstá, 0.5–1 couvá).
    /// </summary>
    public static (double Fraction, double Phase) MoonPhase(DateOnly date)
    {
        var noon = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Utc);
        var c = new Coordinate(0, 0, noon);
        var mi = c.CelestialInfo.MoonIllum;
        return (mi.Fraction, mi.Phase);
    }

    /// <summary>Nejbližší úplněk ode dne <paramref name="from"/> (včetně), hledá do 40 dní.</summary>
    public static DateOnly NextFullMoon(DateOnly from)
    {
        DateOnly best = from; double bestDist = double.MaxValue;
        for (int i = 0; i <= 40; i++)
        {
            var d = from.AddDays(i);
            double dist = Math.Abs(MoonPhase(d).Phase - 0.5);
            if (dist < bestDist) { bestDist = dist; best = d; }
        }
        return best;
    }
}
