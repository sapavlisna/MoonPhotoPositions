using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

[Trait("Category", "Integration")]
public class RaycastTests
{
    const double Lat = 49.04183, Lon = 16.63903;
    static readonly string Cache = Path.Combine(Path.GetTempPath(), "moonapp-test-dsm");

    [Fact]
    public void Drop_MatchesFormula()
    {
        // (1-0.13)*r^2/(2R); pro 5000 m ~ 1,71 m
        Assert.Equal(0.87 * 5000.0 * 5000.0 / (2 * 6_371_000.0), Raycast.Drop(5000), 6);
    }

    [Fact]
    public async Task SnapPeak_MatchesBackend()
    {
        // backend snap(50 m, 1 m): top 368.8, base 354.1, height 14.7, point 368.0, avg 351.1
        var s = await Raycast.SnapPeakAsync(Lat, Lon, 50, 1.0, Cache);
        Assert.True(Math.Abs(s.Top - 368.8) < 2.0, $"top {s.Top}");
        Assert.True(Math.Abs(s.Base - 354.1) < 4.0, $"base {s.Base}");
        Assert.True(Math.Abs(s.Height - 14.7) < 4.0, $"height {s.Height}");
        Assert.True(Math.Abs(s.Avg - 351.1) < 5.0, $"avg {s.Avg}");
        // přesný bod je na úzké věži citlivý na ~3m posun datumu (DotSpatial vs pyproj) —
        // ověříme jen, že je to platná výška v rozsahu okolí [base..top]
        Assert.True(s.Point >= s.Base - 2 && s.Point <= s.Top + 2, $"point {s.Point} mimo [{s.Base}..{s.Top}]");
    }

    [Fact]
    public async Task HorizonProfile_HasFullCircle()
    {
        var dmp = await Cuzk.LoadAroundAsync(Lat, Lon, 1500, 5.0, Cuzk.Dmp, Cache);
        var prof = Raycast.HorizonProfile(dmp, Lat, Lon, 1.7, 10, 1500, 5, 2.0);
        Assert.Equal(180, prof.Length);                     // 360/2
        Assert.Contains(prof, p => p.El > 0);               // někde je překážka nad obzorem
    }
}
