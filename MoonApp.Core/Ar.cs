namespace MoonApp.Core;

/// <summary>
/// Projekce nebeských souřadnic (az/alt) na obrazovku pro AR překryv.
/// Gnómonická (tan) projekce kolem směru pohledu (heading) a náklonu (pitch).
/// Roll (rotace kolem osy pohledu) se neřeší — předpokládá se držení na výšku.
/// </summary>
public static class Ar
{
    /// <summary>(x,y) v px a zda je bod v záběru. heading/pitch/FOV ve stupních.</summary>
    public static (double X, double Y, bool InView) Project(double az, double alt,
        double heading, double pitch, double hFovDeg, double vFovDeg, double width, double height)
    {
        double daz = ((az - heading + 540.0) % 360.0) - 180.0;
        double dalt = alt - pitch;
        bool inView = Math.Abs(daz) < hFovDeg / 2 && Math.Abs(dalt) < vFovDeg / 2;
        double tx = Math.Tan(daz * Math.PI / 180) / Math.Tan(hFovDeg / 2 * Math.PI / 180);
        double ty = Math.Tan(dalt * Math.PI / 180) / Math.Tan(vFovDeg / 2 * Math.PI / 180);
        return (width / 2 + tx * width / 2, height / 2 - ty * height / 2, inView);
    }
}
