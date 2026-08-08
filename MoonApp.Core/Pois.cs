using System.Text;
using System.Text.Json;

namespace MoonApp.Core;

/// <summary>Kandidát na objekt focení z OpenStreetMap.</summary>
public sealed class Poi
{
    public string Id = "", Cat = "";
    public string? Name;
    public double Lat, Lon, DistM;
    public double? OsmH, Ele;
    public bool Notable, Lone;
    /// <summary>O kolik vrchol čouhá nad okolní krajinu [m]; null = nezměřeno.</summary>
    public double? Sky;
    /// <summary>Výška stavby nad terénem [m] z výškopisu.</summary>
    public double? H;
}

/// <summary>
/// Kandidáti na objekt z Overpassu, port server/src/osm.py. Overpass odpoví, *co* v okolí
/// stojí; jak to vypadá, ví až výškopis (<see cref="Raycast.SnapPeakAsync"/>) — proto se tu
/// nefiltruje podle domněnek o zajímavosti.
///
/// Z telefonu jde Overpass volat přímo (na rozdíl od prohlížeče tu neplatí CORS).
/// </summary>
public static class Pois
{
    const string Url = "https://overpass-api.de/api/interpreter";
    // hlavička smí jen ASCII — s diakritikou HttpClient odmítne request ještě před odesláním
    const string UserAgent = "MoonApp/1.5 (personal moon photography planner)";
    public const double MaxRadiusM = 20000;
    const double LoneTreeRM = 150;    // strom bez souseda je kandidát na solitéra
    const double DedupeRM = 40;       // jeden motiv bývá v OSM zakreslený víckrát
    static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    /// <summary>Jeden filtr: tag, operace ("=", "in", "ge") a hodnota.</summary>
    readonly record struct F(string Key, string Op, string[]? Vals = null, double Min = 0, string? Rx = null);

    /// <summary>
    /// Kategorie v pořadí, které rozhoduje o zařazení objektu s víc tagy: rozhledna nesmí
    /// spadnout pod obecnou věž a komín teplárny (má i building=yes) pod budovu.
    /// </summary>
    static readonly (string Slug, string Label, F[][] Filters)[] Cats =
    [
        ("rozhledna", "rozhledna", [[new F("man_made", "=", ["tower"]), new F("tower:type", "=", ["observation"])]]),
        ("vysilac", "vysílač", [
            [new F("man_made", "=", ["communications_tower"])],
            [new F("man_made", "=", ["mast"])],
            [new F("man_made", "=", ["tower"]), new F("tower:type", "in", ["communication", "telecommunication", "BTS"])]]),
        ("vodojem", "vodojem", [[new F("man_made", "=", ["water_tower"])]]),
        ("komin", "komín", [[new F("man_made", "=", ["chimney"])]]),
        ("vez", "věž", [
            [new F("man_made", "=", ["tower"]), new F("tower:type", "in",
                ["bell_tower", "campanile", "watchtower", "aircraft_control", "radar", "cooling", "lighting", "clock"])],
            [new F("aeroway", "=", ["control_tower"])]]),
        ("mlyn", "mlýn", [[new F("man_made", "=", ["windmill"])]]),
        ("kostel", "kostel", [[new F("amenity", "=", ["place_of_worship"])], [new F("historic", "=", ["church"])]]),
        ("hrad", "hrad, zřícenina", [
            [new F("historic", "=", ["castle"])], [new F("historic", "=", ["ruins"])], [new F("historic", "=", ["fort"])]]),
        ("budova", "výšková budova", [
            [new F("building", "in"), new F("height", "ge", Min: 40, Rx: "^(4[0-9]|[5-9][0-9]|[1-9][0-9][0-9])")],
            [new F("building", "in"), new F("building:levels", "ge", Min: 12, Rx: "^(1[2-9]|[2-9][0-9])$")]]),
        ("pomnik", "pomník", [[new F("historic", "=", ["monument"])]]),
        ("vyhlidka", "vyhlídka", [[new F("tourism", "=", ["viewpoint"])]]),
        ("vrchol", "vrchol", [[new F("natural", "=", ["peak"])]]),
        ("strom", "samostatný strom", [[new F("natural", "=", ["tree"])]]),
    ];

    public static IReadOnlyList<(string Slug, string Label)> Categories =>
        [.. Cats.Select(c => (c.Slug, c.Label))];

    static string Selector(F[] flt)
    {
        var sb = new StringBuilder();
        foreach (var f in flt)
            sb.Append(f.Op switch
            {
                "=" => $"[\"{f.Key}\"=\"{f.Vals![0]}\"]",
                "in" => f.Vals is null ? $"[\"{f.Key}\"]" : $"[\"{f.Key}\"~\"^({string.Join('|', f.Vals)})$\"]",
                _ => $"[\"{f.Key}\"~\"{f.Rx}\"]",
            });
        return sb.ToString();
    }

    /// <summary>
    /// Dotaz vždy na všechny kategorie — filtruje se až z cache, ať přepínání nic nestojí.
    /// Čísla povinně invariantně: v českém locale by z „49.19“ bylo „49,19“ a Overpass
    /// by to odmítl jako chybný dotaz.
    /// </summary>
    static string Query(double lat, double lon, double radiusM)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder("[out:json][timeout:120];\n(\n");
        foreach (var (_, _, filters) in Cats)
            foreach (var flt in filters)
                sb.Append(string.Create(inv,
                    $"  nwr(around:{radiusM:F0},{lat:F6},{lon:F6}){Selector(flt)};\n"));
        return sb.Append(");\nout center tags;").ToString();
    }

    static bool Matches(F[] flt, Dictionary<string, string> tags)
    {
        foreach (var f in flt)
        {
            if (!tags.TryGetValue(f.Key, out var tv)) return false;
            if (f.Op == "=" && tv != f.Vals![0]) return false;
            if (f.Op == "in" && f.Vals is not null && !f.Vals.Contains(tv)) return false;
            if (f.Op == "ge" && (Num(tv) is not { } n || n < f.Min)) return false;
        }
        return true;
    }

    /// <summary>Výška z OSM: „25“, „25 m“, „25.5“ → číslo; „~30“ a podobné → null.</summary>
    static double? Num(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().TrimEnd('m', 'M').Trim().Replace(',', '.');
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    static string CachePath(string? dir, double lat, double lon, double radiusM) =>
        Path.Combine(dir ?? Path.Combine(Path.GetTempPath(), "moonapp-osm"),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"pois_{lat:F3}_{lon:F3}_r{(int)radiusM}.json"));

    static async Task<string> RawAsync(double lat, double lon, double radiusM, string? cacheDir,
        bool fresh, CancellationToken ct)
    {
        string path = CachePath(cacheDir, lat, lon, radiusM);
        bool fresh_enough = !fresh && File.Exists(path)
            && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheTtl;
        if (fresh_enough) return await File.ReadAllTextAsync(path, ct);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            using var resp = await http.PostAsync(Url,
                new StringContent(Query(lat, lon, radiusM), Encoding.UTF8, "text/plain"), ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json, ct);
            return json;
        }
        catch when (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, ct);   // prošlá cache je lepší než nic
        }
    }

    /// <summary>Kandidáti v okruhu, seřazení podle vzdálenosti od středu.</summary>
    public static async Task<List<Poi>> FindAsync(double lat, double lon, double radiusM = 10000,
        IEnumerable<string>? cats = null, string? cacheDir = null, bool fresh = false,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        radiusM = Math.Min(radiusM, MaxRadiusM);
        progress?.Report(new("Hledám v OpenStreetMap…"));
        string json = await RawAsync(lat, lon, radiusM, cacheDir, fresh, ct);

        // pořadí podle Cats, ne podle volajícího — na něm závisí zařazení objektu s víc tagy
        var want = cats is null ? null : new HashSet<string>(cats);
        var use = Cats.Where(c => want is null || want.Contains(c.Slug)).ToArray();
        if (use.Length == 0) use = Cats;

        var rows = new List<Poi>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("elements", out var els))
            foreach (var e in els.EnumerateArray())
            {
                var tags = new Dictionary<string, string>();
                if (e.TryGetProperty("tags", out var tg))
                    foreach (var p in tg.EnumerateObject())
                        tags[p.Name] = p.Value.ToString();

                string? cat = null;
                foreach (var c in use)
                    if (c.Filters.Any(f => Matches(f, tags))) { cat = c.Slug; break; }
                if (cat is null) continue;

                double plat, plon;
                if (e.TryGetProperty("lat", out var la) && e.TryGetProperty("lon", out var lo))
                { plat = la.GetDouble(); plon = lo.GetDouble(); }
                else if (e.TryGetProperty("center", out var ce))
                { plat = ce.GetProperty("lat").GetDouble(); plon = ce.GetProperty("lon").GetDouble(); }
                else continue;

                tags.TryGetValue("name", out var name);
                tags.TryGetValue("denotation", out var den);
                rows.Add(new Poi
                {
                    Id = (e.TryGetProperty("type", out var ty) ? ty.GetString()![..1] : "n")
                       + (e.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : ""),
                    Cat = cat,
                    Name = string.IsNullOrWhiteSpace(name) ? null : name,
                    Lat = Math.Round(plat, 7),
                    Lon = Math.Round(plon, 7),
                    OsmH = Num(tags.GetValueOrDefault("height")) ?? Num(tags.GetValueOrDefault("building:height")),
                    Ele = Num(tags.GetValueOrDefault("ele")),
                    Notable = name is not null || tags.ContainsKey("height")
                              || den is "natural_monument" or "landmark",
                    DistM = Math.Round(Geo.Distance(lat, lon, plat, plon)),
                });
            }

        rows = [.. rows.Where(r => r.DistM <= radiusM)];
        MarkLoneTrees([.. rows.Where(r => r.Cat == "strom")]);
        // stromů jsou v OSM tisíce a většina je alej nebo okraj remízku — silueta by splynula
        rows = [.. rows.Where(r => r.Cat != "strom" || r.Lone || r.Notable)];
        rows = Dedupe(rows);
        rows.Sort((a, b) => a.DistM.CompareTo(b.DistM));
        return rows;
    }

    /// <summary>Označí stromy bez jiného mapovaného stromu v okruhu — kandidáty na solitéry.</summary>
    static void MarkLoneTrees(List<Poi> trees)
    {
        if (trees.Count == 0) return;
        var xy = trees.Select(t => Geo.ToSjtsk(t.Lon, t.Lat)).ToArray();
        double r2 = LoneTreeRM * LoneTreeRM;
        for (int i = 0; i < trees.Count; i++)
        {
            bool lone = true;
            for (int j = 0; j < trees.Count && lone; j++)
            {
                if (i == j) continue;
                double dx = xy[i].Item1 - xy[j].Item1, dy = xy[i].Item2 - xy[j].Item2;
                if (dx * dx + dy * dy <= r2) lone = false;
            }
            trees[i].Lone = lone;
        }
    }

    /// <summary>
    /// Sloučí kandidáty blíž než DedupeRM, i napříč kategoriemi: Špilberk je v OSM hrad
    /// (dvakrát), kaple i vyhlídková věž — čtyři položky na jednom kopci, fotograficky jeden
    /// motiv. Zůstane ten, o kterém víme nejvíc (jméno, pak výška, pak bližší).
    /// </summary>
    static List<Poi> Dedupe(List<Poi> rows)
    {
        if (rows.Count == 0) return rows;
        var xy = rows.Select(r => Geo.ToSjtsk(r.Lon, r.Lat)).ToArray();
        var rank = Enumerable.Range(0, rows.Count)
            .OrderBy(i => rows[i].Name is null ? 1 : 0)
            .ThenBy(i => rows[i].OsmH is null ? 1 : 0)
            .ThenBy(i => rows[i].DistM);
        var dead = new bool[rows.Count];
        var keep = new List<Poi>();
        double r2 = DedupeRM * DedupeRM;
        foreach (int i in rank)
        {
            if (dead[i]) continue;
            keep.Add(rows[i]);
            for (int j = 0; j < rows.Count; j++)
            {
                if (j == i) continue;
                double dx = xy[i].Item1 - xy[j].Item1, dy = xy[i].Item2 - xy[j].Item2;
                if (dx * dx + dy * dy <= r2) dead[j] = true;
            }
        }
        return keep;
    }

    /// <summary>
    /// Doměří z výškopisu, o kolik kandidáti čouhají nad okolní krajinu. Overpass o výšce
    /// buď mlčí, nebo lže — tohle je jediné, co rozhoduje, jestli má smysl to fotit.
    /// </summary>
    public static async Task ScoreAsync(IReadOnlyList<Poi> items, string? cacheDir = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new($"Měřím z výškopisu… {i}/{items.Count}", (double)i / items.Count));
            try
            {
                var snap = await Raycast.SnapPeakAsync(items[i].Lat, items[i].Lon, 40, 1.0, cacheDir);
                var ctx = await Cuzk.LoadAroundAsync(items[i].Lat, items[i].Lon, 1000, 10.0, Cuzk.Dmp, cacheDir);
                var area = Raycast.AreaStats(ctx, items[i].Lat, items[i].Lon, 1000, 50);
                items[i].H = snap.Height;
                items[i].Sky = area.Avg50 is { } avg ? Math.Round(snap.Top - avg, 1) : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { items[i].Sky = null; }   // null = měřeno a nepovedlo se
        }
        progress?.Report(new("Hotovo", 1));
    }
}
