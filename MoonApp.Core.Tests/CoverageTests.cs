using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

[Trait("Category", "Integration")]
public class CoverageTests
{
    const double Lat = 49.04183, Lon = 16.63903;
    static readonly string Cache = Path.Combine(Path.GetTempPath(), "moonapp-test-dsm");
    static readonly DateOnly Date = new(2026, 6, 30);

    [Fact]
    public async Task Coverage_MatchesBackend()
    {
        // backend (r2000, res100, days1, subj 14.7): visible 3, aligned 76, max 4, 41x41, top_z 368.76
        var g = await Coverage.ComputeAsync(Lat, Lon, Date, 1, 2000, 100, 1.7, 14.7, cacheDir: Cache);

        Assert.Equal(41, g.NRows);
        Assert.Equal(41, g.NCols);
        Assert.True(Math.Abs(g.TopZ - 368.76) < 1.5, $"top_z {g.TopZ}");
        // normál je u objektu na kopci malý a citlivý → jen rozumné meze
        Assert.InRange(g.Aligned, 50, 110);
        Assert.InRange(g.Visible, 1, 12);
        Assert.True(g.Visible <= g.Aligned);
    }

    [Fact]
    public async Task Coverage_VisOnly_MatchesBackend()
    {
        // backend vis_only: 197 viditelných buněk (jen LOS, robustní)
        var g = await Coverage.ComputeAsync(Lat, Lon, Date, 1, 2000, 100, 1.7, 14.7,
            visOnly: true, cacheDir: Cache);
        Assert.True(g.Max <= 1, $"vis_only max {g.Max}");
        Assert.InRange(g.Visible, 157, 237);   // 197 ± ~20 % (az sféricky vs geodet., ~3m datum, LOS)
    }
}
