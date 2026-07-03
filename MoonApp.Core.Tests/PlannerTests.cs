using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

[Trait("Category", "Integration")]
public class PlannerTests
{
    static readonly string Cache = Path.Combine(Path.GetTempPath(), "moonapp-test-dsm");

    [Fact]
    public async Task Viewpoint_ValleySpot_MatchesBackend()
    {
        // objekt Výhon (vrchol 368.8), stanoviště v údolí (SZ, ~5,76 km)
        // backend: dist 5764, bearing 134.71, el_target 1.393, na špici 22:15 (+02:00), alt 1.62
        var r = await Planner.ViewpointAsync(49.04183, 16.63903, 368.8,
            49.07831, 16.58300, new DateOnly(2026, 6, 30), cacheDir: Cache);

        Assert.True(Math.Abs(r.DistanceM - 5764) < 10, $"dist {r.DistanceM}");
        Assert.True(Math.Abs(((r.Bearing - 134.71 + 540) % 360) - 180) < 0.6, $"bearing {r.Bearing}");
        Assert.True(Math.Abs(r.ElTargetDeg - 1.393) < 0.2, $"el_target {r.ElTargetDeg}");
        Assert.True(r.Clear, "objekt má být z údolí vidět");

        Assert.NotNull(r.OnTipUtc);
        var expUtc = new DateTime(2026, 6, 30, 20, 15, 0, DateTimeKind.Utc);   // 22:15 +02:00
        Assert.True(Math.Abs((r.OnTipUtc!.Value - expUtc).TotalMinutes) <= 15,
            $"na špici {r.OnTipUtc:HH:mm} UTC vs ~20:15");
        Assert.True(r.OnTipAlt is > 0.5 and < 3.0, $"on-tip alt {r.OnTipAlt}");
        Assert.True(r.Horizon.Length == 180 && r.Track.Count > 100);
    }
}
