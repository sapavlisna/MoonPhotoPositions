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
}
