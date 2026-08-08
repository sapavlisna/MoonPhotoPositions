using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

public class AstroTests
{
    // referenční hodnoty ze skyfield (Python astro.track) @ Výhon 49.04183,16.63903.
    // Lokální Europe/Prague (CEST, +02:00) → UTC. Tolerance kryje rozdíl algoritmů + refrakci.
    [Theory]
    [InlineData("2026-06-30T20:00:00Z", 132.60, -0.056)]   // 22:00 +02:00
    [InlineData("2026-06-30T22:30:00Z", 162.863, 12.904)]  // 00:30 +02:00 (další den)
    public void MoonAzAlt_MatchesSkyfield(string isoUtc, double expAz, double expAlt)
    {
        var utc = DateTime.Parse(isoUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var s = Astro.MoonAt(49.04183, 16.63903, utc);
        Assert.True(Math.Abs(((s.Az - expAz + 540) % 360) - 180) < 0.6, $"az {s.Az} vs {expAz}");
        Assert.True(Math.Abs(s.Alt - expAlt) < 1.0, $"alt {s.Alt} vs {expAlt}");
    }

    // Referenční hodnoty ze skyfield (GET /track?body=sun) @ Výhon, lokální čas +02:00.
    // Porovnává se úhlová vzdálenost obou směrů, ne azimut zvlášť: vysoko nad obzorem
    // (v poledne 62°) odpovídá stupeň azimutu jen zlomku stupně na obloze, takže samostatný
    // práh na azimut by tam byl nesmyslně přísný a u obzoru zase benevolentní.
    [Theory]
    [InlineData("2026-06-30T10:00:00Z", 151.349, 61.767)]   // 12:00 +02:00
    [InlineData("2026-06-30T14:00:00Z", 250.953, 45.866)]   // 16:00 +02:00
    [InlineData("2026-06-30T16:00:00Z", 275.522, 26.459)]   // 18:00 +02:00
    public void SunAzAlt_MatchesSkyfield(string isoUtc, double expAz, double expAlt)
    {
        var utc = DateTime.Parse(isoUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var s = Astro.At(Body.Sun, 49.04183, 16.63903, utc);
        double sep = AngularSep(s.Az, s.Alt, expAz, expAlt);
        Assert.True(sep < 1.0, $"odchylka {sep:F2}° · az {s.Az:F2} vs {expAz}, alt {s.Alt:F2} vs {expAlt}");
    }

    /// <summary>Úhlová vzdálenost dvou směrů na obloze [°].</summary>
    static double AngularSep(double az1, double alt1, double az2, double alt2)
    {
        double r = Math.PI / 180;
        double c = Math.Sin(alt1 * r) * Math.Sin(alt2 * r)
                 + Math.Cos(alt1 * r) * Math.Cos(alt2 * r) * Math.Cos((az1 - az2) * r);
        return Math.Acos(Math.Clamp(c, -1, 1)) / r;
    }

    /// <summary>Slunce a Měsíc nesmí vrátit tutéž dráhu — záměna těles je tichá a zrádná chyba.</summary>
    [Fact]
    public void SunAndMoon_Differ()
    {
        var utc = new DateTime(2026, 6, 30, 14, 0, 0, DateTimeKind.Utc);
        var sun = Astro.At(Body.Sun, 49.04183, 16.63903, utc);
        var moon = Astro.At(Body.Moon, 49.04183, 16.63903, utc);
        Assert.True(Math.Abs(sun.Az - moon.Az) > 5 || Math.Abs(sun.Alt - moon.Alt) > 5);
    }

    [Fact]
    public void Track_ProducesContiguousSamples()
    {
        var from = new DateTime(2026, 6, 30, 20, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(2);
        var track = Astro.Track(49.04183, 16.63903, from, to, 30);
        Assert.Equal(5, track.Count);        // 0,30,60,90,120 min
        Assert.True(track[^1].Alt > track[0].Alt);  // Měsíc po východu stoupá
    }
}
