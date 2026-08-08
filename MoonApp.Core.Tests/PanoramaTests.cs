using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

/// <summary>
/// Panorama staví na stejném pochodu terénem jako <see cref="Raycast.HorizonProfile"/>,
/// takže musí dávat tutéž siluetu — jen navíc ví, jak je překážka daleko.
/// </summary>
public class PanoramaTests
{
    const double Lat = 49.04183, Lon = 16.63903;
    static string Cache => Path.Combine(Path.GetTempPath(), "moonapp-tests-dsm");

    [Fact]
    public async Task Skyline_MatchesHorizonProfile()
    {
        var dmp = await Cuzk.LoadAroundAsync(Lat, Lon, 1600, 5.0, Cuzk.Dmp, Cache);
        var horizon = Raycast.HorizonProfile(dmp, Lat, Lon, 1.7, 20, 1500, 10, 1.0);
        var sky = Panorama.Skyline(dmp, Lat, Lon, 1.7, 20, 1500, 10, 0, 360, 1.0);

        Assert.Equal(horizon.Length, sky.Length);
        for (int i = 0; i < horizon.Length; i++)
        {
            Assert.Equal(horizon[i].Az, sky[i].Az, 6);
            Assert.True(Math.Abs(horizon[i].El - sky[i].El) < 1e-6,
                $"az {horizon[i].Az}: elevace {sky[i].El} vs {horizon[i].El}");
        }
    }

    [Fact]
    public async Task Skyline_ReportsPlausibleDistances()
    {
        var dmp = await Cuzk.LoadAroundAsync(Lat, Lon, 1600, 5.0, Cuzk.Dmp, Cache);
        var sky = Panorama.Skyline(dmp, Lat, Lon, 1.7, 20, 1500, 10, 0, 360, 2.0);
        Assert.All(sky, s => Assert.InRange(s.DistM, 20, 1500));
        // z vrcholu kopce je aspoň někde vidět daleko, jinde blízko — jinak by hloubka nešla obarvit
        Assert.True(sky.Max(s => s.DistM) - sky.Min(s => s.DistM) > 100);
    }

    /// <summary>Interpolace siluety musí sedět na vzorky, jinak by se hory posunuly proti dráze.</summary>
    [Fact]
    public async Task ElAt_InterpolatesOnSamples()
    {
        var dmp = await Cuzk.LoadAroundAsync(Lat, Lon, 1600, 5.0, Cuzk.Dmp, Cache);
        var sky = Panorama.Skyline(dmp, Lat, Lon, 1.7, 20, 1500, 10, 0, 360, 1.0);
        for (int i = 0; i < sky.Length; i += 37)
            Assert.Equal(sky[i].El, Panorama.ElAt(sky, sky[i].Az), 6);
    }

    [Fact]
    public void BodyRadius_IsHalfADegree()
    {
        Assert.InRange(Panorama.BodyRadiusDeg(Body.Moon) * 2, 0.4, 0.6);
        Assert.InRange(Panorama.BodyRadiusDeg(Body.Sun) * 2, 0.4, 0.6);
    }
}
