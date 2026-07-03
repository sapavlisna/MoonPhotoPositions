namespace MoonApp.Core;

/// <summary>Fáze dlouhého výpočtu + volitelný podíl dokončení (0..1). Fraction=null → neurčitá fáze.</summary>
public readonly record struct ProgressInfo(string Stage, double? Fraction = null);
