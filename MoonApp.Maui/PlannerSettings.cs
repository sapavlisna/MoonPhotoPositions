namespace MoonApp.Maui;

/// <summary>Parametry plánovače (odpovídají polím webového plánovače).</summary>
public class PlannerSettings
{
    public DateTime Date { get; set; } = DateTime.Today;
    public double RadiusM { get; set; } = 5000;      // okruh hledání
    public double RadiusMinM { get; set; } = 0;      // vnitřní okruh (mezikruží) — vyloučí bližší
    public double ResM { get; set; } = 80;           // krok mřížky
    public double EyeH { get; set; } = 1.7;          // výška očí
    public double SubjectMinH { get; set; } = 10;    // min. výška objektu (když snap nic nenajde)
    public double AzTol { get; set; } = 2;           // tolerance azimutu
    public double AltBand { get; set; } = 2;         // výškový pás Měsíce
}
