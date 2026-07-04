using CoordinateSharp;

namespace MoonApp.Core;

/// <summary>Jeden vzorek dráhy Měsíce: čas (UTC), azimut a výška [°].</summary>
public readonly record struct MoonSample(DateTime TimeUtc, double Az, double Alt);

/// <summary>
/// Astronomie Měsíce na zařízení přes CoordinateSharp (nahrazuje skyfield + de421.bsp z
/// Python backendu, src/astro.py). Vstupy v UTC.
/// </summary>
public static class Astro
{
    /// <summary>Azimut [°] a výška [°] Měsíce z místa v daný UTC čas.</summary>
    public static MoonSample MoonAt(double lat, double lon, DateTime utc)
    {
        var c = new Coordinate(lat, lon, DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return new MoonSample(utc, c.CelestialInfo.MoonAzimuth, c.CelestialInfo.MoonAltitude);
    }

    /// <summary>Dráha Měsíce po krocích stepMin [min] v intervalu [fromUtc, toUtc].</summary>
    public static List<MoonSample> Track(double lat, double lon, DateTime fromUtc, DateTime toUtc, double stepMin)
    {
        var list = new List<MoonSample>();
        for (var t = fromUtc; t <= toUtc; t = t.AddMinutes(stepMin))
            list.Add(MoonAt(lat, lon, t));
        return list;
    }

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
