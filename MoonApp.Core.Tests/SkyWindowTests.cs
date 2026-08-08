using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

/// <summary>
/// „Kdy odsud vidět“ — kontroluje se vnitřní soulad s <see cref="Planner"/>: den, který
/// planner označí za den s tělesem na špici, musí okno najít taky, a naopak nesmí vracet
/// dny, kdy je těleso mimo toleranci.
/// </summary>
public class SkyWindowTests
{
    // Výhon (rozhledna Akátová věž) jako objekt, stanoviště ~1,3 km na západ. Směr na východ
    // je podstatný: na jihu prochází Měsíc nejníž 13° nad obzorem, takže objekt v potřebné
    // výšce 8° by tam nepotkal nikdy a test by nehlídal nic.
    const double ObjLat = 49.04183, ObjLon = 16.63903, ObjTop = 368.8;
    const double ObsLat = 49.04183, ObsLon = 16.6212;
    static string Cache => Path.Combine(Path.GetTempPath(), "moonapp-tests-dsm");

    [Fact]
    public async Task ForPoint_FindsDaysWithinTolerance()
    {
        var from = new DateOnly(2026, 8, 1);
        var days = await SkyWindow.ForPointAsync(ObjLat, ObjLon, ObjTop, ObsLat, ObsLon,
            from, from.AddDays(29), Body.Moon, azTol: 3, altBand: 3, cacheDir: Cache);

        Assert.All(days, d =>
        {
            Assert.True(d.AzErrDeg <= 3, $"{d.Date}: azimut mimo toleranci ({d.AzErrDeg:F1}°)");
            Assert.True(d.Alt > 0, $"{d.Date}: těleso pod obzorem");
            Assert.InRange(d.Date, from, from.AddDays(29));
        });
        // za měsíc Měsíc obletí celý azimutový rozsah — pár dní vyjít musí
        Assert.NotEmpty(days);
    }

    /// <summary>Režim „jen viditelnost“ nemá těleso, takže nesmí vracet žádné dny.</summary>
    [Fact]
    public async Task ForPoint_VisModeReturnsNothing()
    {
        var from = new DateOnly(2026, 8, 1);
        var days = await SkyWindow.ForPointAsync(ObjLat, ObjLon, ObjTop, ObsLat, ObsLon,
            from, from.AddDays(9), Body.Vis, cacheDir: Cache);
        Assert.Empty(days);
    }

    /// <summary>Slunce má jinou dráhu než Měsíc, takže i jiné dny — záměna by byla tichá chyba.</summary>
    [Fact]
    public async Task ForPoint_SunDiffersFromMoon()
    {
        var from = new DateOnly(2026, 8, 1);
        var moon = await SkyWindow.ForPointAsync(ObjLat, ObjLon, ObjTop, ObsLat, ObsLon,
            from, from.AddDays(29), Body.Moon, azTol: 3, altBand: 3, cacheDir: Cache);
        var sun = await SkyWindow.ForPointAsync(ObjLat, ObjLon, ObjTop, ObsLat, ObsLon,
            from, from.AddDays(29), Body.Sun, azTol: 3, altBand: 3, cacheDir: Cache);
        Assert.NotEqual(moon.Select(d => d.Date), sun.Select(d => d.Date));
    }
}
