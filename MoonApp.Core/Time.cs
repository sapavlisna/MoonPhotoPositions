namespace MoonApp.Core;

/// <summary>Časové pásmo plánovače (Europe/Prague), s fallbackem na Windows ID.</summary>
public static class Time
{
    public static readonly TimeZoneInfo Prague = Resolve();

    static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Europe/Prague", "Central European Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Utc;
    }
}
