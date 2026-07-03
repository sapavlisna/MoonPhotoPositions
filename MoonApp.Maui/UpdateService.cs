using System.Text.Json;

namespace MoonApp.Maui;

/// <summary>Kontrola nové verze přes GitHub Releases API. Volá se při startu i ručně z nastavení.</summary>
static class UpdateService
{
    const string OwnerRepo = "sapavlisna/MoonPhotoPositions";
    const string SkipKey = "skipped_update_version";

    /// <param name="manual">true = spuštěno z nastavení (hlásí i „máš nejnovější" a ignoruje přeskočení).</param>
    public static async Task CheckAsync(Page host, bool manual)
    {
        string tag, url;
        Version? latest;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MoonApp");
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{OwnerRepo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            latest = ParseVer(tag);
            url = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if ((a.TryGetProperty("name", out var n) ? n.GetString() : null)?.EndsWith(".apk") == true)
                    { url = a.GetProperty("browser_download_url").GetString() ?? url; break; }
        }
        catch
        {
            if (manual) await host.DisplayAlertAsync("Aktualizace", "Nepodařilo se ověřit (jsi offline?).", "OK");
            return;
        }

        var current = ParseVer(AppInfo.Current.VersionString);
        if (latest is null || current is null || latest <= current)
        {
            if (manual) await host.DisplayAlertAsync("Aktualizace",
                $"Máš nejnovější verzi ({AppInfo.Current.VersionString}).", "OK");
            return;
        }

        // při startu respektuj „přeskočit tuto verzi"; ruční kontrola ji ignoruje
        if (!manual && Preferences.Default.Get(SkipKey, "") == tag) return;

        string choice = await host.DisplayActionSheetAsync(
            $"Nová verze {tag} (máš {AppInfo.Current.VersionString})",
            "Později", null, "Stáhnout", "Přeskočit tuto verzi");
        if (choice == "Stáhnout")
        {
            if (!string.IsNullOrEmpty(url)) await Launcher.OpenAsync(url);
        }
        else if (choice == "Přeskočit tuto verzi")
        {
            Preferences.Default.Set(SkipKey, tag);
        }
        // „Později" / zavření → nic (ukáže se zase příště)
    }

    static Version? ParseVer(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out var v) ? v : null;
    }
}
