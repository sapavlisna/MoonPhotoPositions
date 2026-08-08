namespace MoonApp.Core;

/// <summary>
/// Jména těles pro hlášky. Drží se webové terminologie (server/web/index.html: BODY),
/// aby appka a plánovač mluvily stejně.
/// </summary>
public static class Names
{
    /// <summary>1. pád: „Měsíc“, „Slunce“, „objekt“.</summary>
    public static string Nom(Body b) => b switch
    {
        Body.Sun => "Slunce",
        Body.Vis => "objekt",
        _ => "Měsíc",
    };

    /// <summary>2. pád: „dráhu Měsíce“, „dráhu Slunce“.</summary>
    public static string Gen(Body b) => b switch
    {
        Body.Sun => "Slunce",
        Body.Vis => "objektu",
        _ => "Měsíce",
    };

    /// <summary>7. pád: „zarovnání s Měsícem“, „se Sluncem“.</summary>
    public static string Ins(Body b) => b switch
    {
        Body.Sun => "se Sluncem",
        Body.Vis => "s objektem",
        _ => "s Měsícem",
    };
}
