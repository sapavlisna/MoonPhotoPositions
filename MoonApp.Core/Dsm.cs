namespace MoonApp.Core;

/// <summary>
/// Rastr výšek v EPSG:5514 (float[] + původ/cell) s bilineárním vzorkováním.
/// Ekvivalent raycast.DSM z Python backendu. Origin = levý horní roh (xmin, ymax),
/// řádek 0 nahoře, y klesá s řádkem.
/// </summary>
public sealed class Dsm
{
    public readonly float[] Data;
    public readonly int Width, Height;
    public readonly double OriginX, OriginY, Cell;

    public Dsm(float[] data, int width, int height, double originX, double originY, double cell)
    {
        Data = data; Width = width; Height = height;
        OriginX = originX; OriginY = originY; Cell = cell;
    }

    /// <summary>Bilineární výška [m] v bodě (x,y) EPSG:5514; NaN mimo rastr nebo na nodata.</summary>
    public double Sample(double x, double y)
    {
        double fcol = (x - OriginX) / Cell - 0.5;
        double frow = (OriginY - y) / Cell - 0.5;
        int c0 = (int)Math.Floor(fcol), r0 = (int)Math.Floor(frow);
        if (c0 < 0 || c0 >= Width - 1 || r0 < 0 || r0 >= Height - 1) return double.NaN;
        double dx = fcol - c0, dy = frow - r0;
        double v00 = Data[r0 * Width + c0], v01 = Data[r0 * Width + c0 + 1];
        double v10 = Data[(r0 + 1) * Width + c0], v11 = Data[(r0 + 1) * Width + c0 + 1];
        double top = v00 * (1 - dx) + v01 * dx;
        double bot = v10 * (1 - dx) + v11 * dx;
        double val = top * (1 - dy) + bot * dy;
        return Math.Abs(val) > 1e5 ? double.NaN : val;   // guard na nodata sentinel
    }

    /// <summary>Nejvyšší hodnota v rastru (ignoruje nodata) — pro snap na vrchol.</summary>
    public (int Col, int Row, double Value) ArgMax()
    {
        int bi = -1; double best = double.NegativeInfinity;
        for (int i = 0; i < Data.Length; i++)
        {
            double v = Data[i];
            if (Math.Abs(v) > 1e5) continue;
            if (v > best) { best = v; bi = i; }
        }
        return bi < 0 ? (-1, -1, double.NaN) : (bi % Width, bi / Width, best);
    }

    /// <summary>Střed pixelu (col,row) v EPSG:5514.</summary>
    public (double X, double Y) PixelCenter(int col, int row)
        => (OriginX + (col + 0.5) * Cell, OriginY - (row + 0.5) * Cell);
}
