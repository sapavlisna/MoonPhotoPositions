using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

/// <summary>
/// Integrační testy — reálně stahují z ČÚZK (potřeba síť). Ověřují celou cestu na zařízení:
/// exportImage F32 → LibTiff dekód → bilineární vzorkování, proti hodnotám z Python backendu.
/// </summary>
[Trait("Category", "Integration")]
public class CuzkTests
{
    const double Lat = 49.04183, Lon = 16.63903;   // Akátová věž na Výhonu
    static readonly string Cache = Path.Combine(Path.GetTempPath(), "moonapp-test-dsm");

    [Fact]
    public async Task Dmr5g_TerrainAtObject_MatchesBackend()
    {
        // backend: identify_elev(DMR5G) = 354.06 m
        var dsm = await Cuzk.LoadAroundAsync(Lat, Lon, 150, 5.0, Cuzk.Dmr5g, Cache);
        var (x, y) = Geo.ToSjtsk(Lon, Lat);
        double z = dsm.Sample(x, y);
        Assert.True(Math.Abs(z - 354.06) < 5.0, $"terén {z} vs ~354.06");
    }

    [Fact]
    public async Task Dmp_PeakInNeighborhood_MatchesBackend()
    {
        // backend snap: top (DMP max v 50 m) = 368.8 m
        var dsm = await Cuzk.LoadAroundAsync(Lat, Lon, 60, 1.0, Cuzk.Dmp, Cache);
        var (_, _, top) = dsm.ArgMax();
        Assert.True(Math.Abs(top - 368.8) < 2.0, $"vrchol {top} vs ~368.8");
    }

    [Fact]
    public async Task Identify_TerrainAtObject_MatchesBackend()
    {
        double? z = await Cuzk.IdentifyElevAsync(Lat, Lon, Cuzk.Dmr5g);
        Assert.NotNull(z);
        Assert.True(Math.Abs(z!.Value - 354.06) < 3.0, $"identify {z} vs ~354.06");
    }
}
