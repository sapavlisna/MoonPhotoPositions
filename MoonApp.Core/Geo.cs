using DotSpatial.Projections;

namespace MoonApp.Core;

/// <summary>
/// Souřadnice a geometrie. Projekce WGS84 (EPSG:4326) ⇄ S-JTSK Křovák (EPSG:5514) a
/// sférická geometrie (pravé azimuty / vzdálenosti) — ekvivalent pyproj Transformer + Geod
/// z Python backendu (src/raycast.py). Pravé azimuty počítáme sféricky, vzorkování pak v 5514.
/// </summary>
public static class Geo
{
    public const double EarthRadiusM = 6_371_000.0;

    private static readonly ProjectionInfo Wgs84 = KnownCoordinateSystems.Geographic.World.WGS1984;
    private static readonly ProjectionInfo Sjtsk = CreateSjtsk();

    private static ProjectionInfo CreateSjtsk()
    {
        try { return ProjectionInfo.FromEpsgCode(5514); }
        catch
        {
            // EPSG:5514 = S-JTSK / Krovak East North (osy E,N; v ČR záporné hodnoty)
            return ProjectionInfo.FromProj4String(
                "+proj=krovak +lat_0=49.5 +lon_0=24.8333333333333 +alpha=30.2881397527778 " +
                "+k=0.9999 +x_0=0 +y_0=0 +ellps=bessel " +
                "+towgs84=589,76,480,0,0,0,0 +units=m +no_defs");
        }
    }

    /// <summary>(lon,lat) WGS84 → (x,y) v EPSG:5514 [m].</summary>
    public static (double X, double Y) ToSjtsk(double lon, double lat)
    {
        double[] xy = { lon, lat };
        double[] z = { 0 };
        Reproject.ReprojectPoints(xy, z, Wgs84, Sjtsk, 0, 1);
        return (xy[0], xy[1]);
    }

    /// <summary>(x,y) EPSG:5514 → (lon,lat) WGS84 [°].</summary>
    public static (double Lon, double Lat) ToWgs84(double x, double y)
    {
        double[] xy = { x, y };
        double[] z = { 0 };
        Reproject.ReprojectPoints(xy, z, Sjtsk, Wgs84, 0, 1);
        return (xy[0], xy[1]);
    }

    private static double Rad(double d) => d * Math.PI / 180.0;
    private static double Deg(double r) => r * 180.0 / Math.PI;

    /// <summary>Pravý azimut [°] z (lat1,lon1) do (lat2,lon2). 0 = sever, po směru hod. ručiček.</summary>
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        double f1 = Rad(lat1), f2 = Rad(lat2), dl = Rad(lon2 - lon1);
        double y = Math.Sin(dl) * Math.Cos(f2);
        double x = Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl);
        return (Deg(Math.Atan2(y, x)) + 360.0) % 360.0;
    }

    /// <summary>Vzdálenost [m] po velké kružnici (haversine).</summary>
    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        double f1 = Rad(lat1), f2 = Rad(lat2), df = Rad(lat2 - lat1), dl = Rad(lon2 - lon1);
        double a = Math.Sin(df / 2) * Math.Sin(df / 2)
                 + Math.Cos(f1) * Math.Cos(f2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return EarthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Cílový bod ve vzdálenosti d [m] a azimutu brng [°] od (lat,lon).</summary>
    public static (double Lat, double Lon) Destination(double lat, double lon, double brng, double d)
    {
        double dr = d / EarthRadiusM, th = Rad(brng), f1 = Rad(lat), l1 = Rad(lon);
        double f2 = Math.Asin(Math.Sin(f1) * Math.Cos(dr) + Math.Cos(f1) * Math.Sin(dr) * Math.Cos(th));
        double l2 = l1 + Math.Atan2(Math.Sin(th) * Math.Sin(dr) * Math.Cos(f1),
                                    Math.Cos(dr) - Math.Sin(f1) * Math.Sin(f2));
        return (Deg(f2), ((Deg(l2) + 540.0) % 360.0) - 180.0);
    }
}
