using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

public class GeoTests
{
    // referenční hodnoty z pyproj (Python backend): _TO5514.transform(lon, lat)
    [Theory]
    [InlineData(16.63903, 49.04183, -597738.50, -1177942.58)]
    [InlineData(16.58300, 49.07831, -601373.43, -1173469.51)]
    public void ToSjtsk_MatchesPyproj(double lon, double lat, double ex, double ey)
    {
        // ~3 m odchylka od pyproj = jiné parametry datového posunu (DotSpatial vs PROJ);
        // při 5m rastru a vnitřně konzistentním použití (bbox i vzorkování ve stejné 5514) zanedbatelné.
        var (x, y) = Geo.ToSjtsk(lon, lat);
        Assert.True(Math.Abs(x - ex) < 5.0, $"x={x} vs {ex}");
        Assert.True(Math.Abs(y - ey) < 5.0, $"y={y} vs {ey}");
    }

    [Theory]
    [InlineData(16.63903, 49.04183)]
    [InlineData(16.58300, 49.07831)]
    public void RoundTrip_WithinCentimeter(double lon, double lat)
    {
        var (x, y) = Geo.ToSjtsk(lon, lat);
        var (lon2, lat2) = Geo.ToWgs84(x, y);
        Assert.True(Math.Abs(lon2 - lon) < 1e-6, $"lon {lon2} vs {lon}");
        Assert.True(Math.Abs(lat2 - lat) < 1e-6, $"lat {lat2} vs {lat}");
    }

    [Fact]
    public void Bearing_And_Distance_ObjectToViewpoint()
    {
        // objekt Výhon → stanoviště v údolí (SZ): směr ~315°, vzdálenost ~5764 m
        double br = Geo.Bearing(49.04183, 16.63903, 49.07831, 16.58300);
        double d = Geo.Distance(49.04183, 16.63903, 49.07831, 16.58300);
        Assert.True(Math.Abs(((br - 315.0 + 540) % 360) - 180) < 2.0, $"bearing {br}");
        Assert.True(Math.Abs(d - 5764.0) < 60.0, $"dist {d}");
    }

    [Fact]
    public void Destination_IsInverseOfBearingDistance()
    {
        var (lat2, lon2) = Geo.Destination(49.04183, 16.63903, 315.0, 5764.0);
        double back = Geo.Distance(49.04183, 16.63903, lat2, lon2);
        Assert.True(Math.Abs(back - 5764.0) < 1.0, $"roundtrip dist {back}");
    }
}
