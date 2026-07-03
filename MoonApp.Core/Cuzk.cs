using System.Globalization;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;

namespace MoonApp.Core;

/// <summary>
/// Klient veřejné ČÚZK ArcGIS ImageServer (3D/dmp = povrch, 3D/dmr5g = terén).
/// Stahuje výškopis přímo ze zařízení (HttpClient → bez CORS) a parsuje F32 GeoTIFF.
/// Ekvivalent src/cuzk.py. Volitelná disková cache (offline reuse).
/// </summary>
public static class Cuzk
{
    public const string Dmp = "dmp";       // povrch (budovy, stromy)
    public const string Dmr5g = "dmr5g";   // holý terén

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    static string Base(string model) =>
        $"https://ags.cuzk.gov.cz/arcgis/rest/services/3D/{model}/ImageServer";

    /// <summary>Výška [m] v jednom bodě (WGS84) přes <c>identify</c>. Null = NoData.</summary>
    public static async Task<double?> IdentifyElevAsync(double lat, double lon, string model)
    {
        string geom = $"{{\"x\":{lon.ToString(CultureInfo.InvariantCulture)}," +
                      $"\"y\":{lat.ToString(CultureInfo.InvariantCulture)}," +
                      "\"spatialReference\":{\"wkid\":4326}}";
        string url = $"{Base(model)}/identify?f=json&geometryType=esriGeometryPoint" +
                     $"&geometry={Uri.EscapeDataString(geom)}&returnGeometry=false";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("value", out var v)) return null;
        if (v.ValueKind == JsonValueKind.String)
            return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    /// <summary>
    /// Stáhne (či načte z cache) rastrové okno výšek v EPSG:5514. bbox v metrech 5514.
    /// </summary>
    public static async Task<Dsm> ExportDsmAsync(double xmin, double ymin, double xmax, double ymax,
        double cellM, string model, string? cacheDir = null, int maxPx = 4000)
    {
        int w = Math.Min(maxPx, Math.Max(1, (int)Math.Round((xmax - xmin) / cellM)));
        int h = Math.Min(maxPx, Math.Max(1, (int)Math.Round((ymax - ymin) / cellM)));

        byte[] tiff;
        string? cachePath = cacheDir is null ? null
            : Path.Combine(cacheDir, $"win_{model}_{xmin:0}_{ymax:0}_w{w}_h{h}.tif");
        if (cachePath is not null && File.Exists(cachePath))
        {
            tiff = await File.ReadAllBytesAsync(cachePath);
        }
        else
        {
            string bbox = string.Join(",", new[] { xmin, ymin, xmax, ymax }
                .Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
            string url = $"{Base(model)}/exportImage?f=image&format=tiff&bbox={bbox}" +
                         $"&bboxSR=5514&imageSR=5514&size={w},{h}&pixelType=F32" +
                         "&interpolation=RSP_BilinearInterpolation";
            tiff = await Http.GetByteArrayAsync(url);
            if (cachePath is not null)
            {
                Directory.CreateDirectory(cacheDir!);
                await File.WriteAllBytesAsync(cachePath, tiff);
            }
        }

        float[] data = DecodeF32Tiff(tiff, out int tw, out int th);
        return new Dsm(data, tw, th, xmin, ymax, cellM);
    }

    /// <summary>Pohodlnější varianta: okolí bodu (lat,lon) o poloměru radius_m.</summary>
    public static async Task<Dsm> LoadAroundAsync(double lat, double lon, double radiusM,
        double cellM, string model, string? cacheDir = null)
    {
        var (x0, y0) = Geo.ToSjtsk(lon, lat);
        return await ExportDsmAsync(x0 - radiusM, y0 - radiusM, x0 + radiusM, y0 + radiusM,
            cellM, model, cacheDir);
    }

    /// <summary>Rozparsuje F32 GeoTIFF (stripovaný i tile-ovaný) na float[] (row-major).</summary>
    static float[] DecodeF32Tiff(byte[] bytes, out int width, out int height)
    {
        using var ms = new MemoryStream(bytes);
        using Tiff tif = Tiff.ClientOpen("mem", "r", ms, new TiffStream())
            ?? throw new InvalidDataException("Nelze otevřít TIFF z ČÚZK.");
        width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var data = new float[width * height];

        if (tif.IsTiled())
        {
            int tw = tif.GetField(TiffTag.TILEWIDTH)[0].ToInt();
            int th = tif.GetField(TiffTag.TILELENGTH)[0].ToInt();
            var tile = new byte[tif.TileSize()];
            for (int y0 = 0; y0 < height; y0 += th)
                for (int x0 = 0; x0 < width; x0 += tw)
                {
                    tif.ReadTile(tile, 0, x0, y0, 0, 0);
                    for (int ty = 0; ty < th && y0 + ty < height; ty++)
                        for (int tx = 0; tx < tw && x0 + tx < width; tx++)
                            data[(y0 + ty) * width + x0 + tx] =
                                BitConverter.ToSingle(tile, (ty * tw + tx) * 4);
                }
        }
        else
        {
            var scan = new byte[tif.ScanlineSize()];
            for (int row = 0; row < height; row++)
            {
                tif.ReadScanline(scan, row);
                Buffer.BlockCopy(scan, 0, data, row * width * 4, width * 4);
            }
        }
        return data;
    }
}
