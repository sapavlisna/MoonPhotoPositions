using MoonApp.Core;
using Xunit;

namespace MoonApp.Core.Tests;

/// <summary>
/// Parita s Python backendem (GET /pois). Overpass je živá služba, takže se neporovnávají
/// přesné počty, ale to, na čem parita stojí: stejné zařazení do kategorií, stejné
/// slučování duplicit a stejný odsev alejí.
/// </summary>
public class PoisTests
{
    // Brno-Špilberk, okruh 3 km. Referenční běh /pois vrátil 224 položek v 11 kategoriích,
    // nejbližší je vrchol Špilberk ~17 m od středu.
    const double Lat = 49.19472, Lon = 16.59944, R = 3000;

    static string CacheDir => Path.Combine(Path.GetTempPath(), "moonapp-tests-osm");

    [Fact]
    public async Task Find_ReturnsSpilberkAndSaneCategories()
    {
        var items = await Pois.FindAsync(Lat, Lon, R, cacheDir: CacheDir);
        Assert.NotEmpty(items);

        // pořadí podle vzdálenosti a nejbližší je vrchol Špilberku
        for (int i = 1; i < items.Count; i++)
            Assert.True(items[i].DistM >= items[i - 1].DistM, "seznam není seřazený podle vzdálenosti");
        Assert.True(items[0].DistM < 100, $"nejbližší kandidát je {items[0].DistM} m daleko");

        // kategorie jsou jen ty známé a v okruhu 3 km jich musí být víc než hrst
        var known = Pois.Categories.Select(c => c.Slug).ToHashSet();
        Assert.All(items, p => Assert.Contains(p.Cat, known));
        Assert.True(items.Select(p => p.Cat).Distinct().Count() >= 6);
        Assert.All(items, p => Assert.True(p.DistM <= R));
    }

    /// <summary>Špilberk je v OSM zakreslený víckrát; slučování musí fungovat i napříč kategoriemi.</summary>
    [Fact]
    public async Task Find_MergesDuplicatesOnOneSpot()
    {
        var items = await Pois.FindAsync(Lat, Lon, R, cacheDir: CacheDir);
        foreach (var a in items)
        {
            var (ax, ay) = Geo.ToSjtsk(a.Lon, a.Lat);
            foreach (var b in items)
            {
                if (ReferenceEquals(a, b)) continue;
                var (bx, by) = Geo.ToSjtsk(b.Lon, b.Lat);
                double d = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
                Assert.True(d > 39, $"dva kandidáti {d:F0} m od sebe ({a.Cat}/{b.Cat}) — dedupe neproběhl");
            }
        }
    }

    /// <summary>Filtr kategorií nesmí propustit nic jiného.</summary>
    [Fact]
    public async Task Find_HonorsCategoryFilter()
    {
        var items = await Pois.FindAsync(Lat, Lon, R, cats: ["kostel", "vez"], cacheDir: CacheDir);
        Assert.NotEmpty(items);
        Assert.All(items, p => Assert.Contains(p.Cat, new[] { "kostel", "vez" }));
    }
}
