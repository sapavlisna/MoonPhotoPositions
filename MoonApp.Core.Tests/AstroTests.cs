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
