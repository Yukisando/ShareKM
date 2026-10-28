using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShareKM;

// Updates from the latest GitHub release: download ShareKM.exe next to the running one, check its SHA-256
// against the digest GitHub publishes, swap the files (a running exe can be renamed, not overwritten) and restart.
static class Updater
{
    public const string Repo = "Yukisando/ShareKM", AssetName = "ShareKM.exe";
    public sealed record Release(Version Version, string Tag, string Url, string? Sha256, long Size);

    public static Version Current { get; } = Parse(Application.ProductVersion) ?? new Version(0, 0);

    static readonly HttpClient Http = MakeClient(); // after Current: static fields initialise in order

    static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ShareKM", Current.ToString()));
        return h;
    }

    static string ExePath => Environment.ProcessPath!;
    static string OldPath => ExePath + ".old";
    static string NewPath => ExePath + ".new";

    // "v1.2.0", "1.2.0+abc" -> 1.2.0
    public static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    // The latest release, or null if it has no ShareKM.exe. Throws on network errors.
    public static async Task<Release?> Latest(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (Parse(tag) is not Version v || !root.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..].ToLowerInvariant() : null;
            return new Release(v, tag!, a.GetProperty("browser_download_url").GetString()!, sha, a.GetProperty("size").GetInt64());
        }
        return null;
    }

    // Downloads and swaps in the new exe. Returns an error message, or null when the new exe is in place
    // (the caller then restarts). progress gets 0..1.
    public static async Task<string?> Install(Release r, IProgress<double>? progress = null)
    {
        try
        {
            try { File.Delete(NewPath); } catch { }
            using (var res = await Http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? r.Size;
                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(NewPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
                }
            }
            if (r.Sha256 != null)
            {
                await using var f = File.OpenRead(NewPath);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(f));
                if (hash != r.Sha256)
                {
                    File.Delete(NewPath);
                    return "The download was damaged (checksum mismatch). Try again.";
                }
            }
            try { File.Delete(OldPath); } catch { }
            File.Move(ExePath, OldPath);
            try { File.Move(NewPath, ExePath); }
            catch { File.Move(OldPath, ExePath); throw; }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            try { File.Delete(NewPath); } catch { }
            return $"ShareKM cannot write to {Path.GetDirectoryName(ExePath)}. Move ShareKM.exe to a folder you own, or turn on Run as administrator.";
        }
        catch (Exception e)
        {
            try { File.Delete(NewPath); } catch { }
            return "Update failed: " + e.Message;
        }
    }

    // Starts the new exe at the same elevation as this copy; it waits for our single-instance lock.
    public static string? StartNew()
    {
        Elevation.SetMarker();
        try
        {
            Process.Start(new ProcessStartInfo(ExePath, Elevation.RelaunchFlag) { UseShellExecute = false })?.Dispose();
            return null;
        }
        catch (Exception e)
        {
            Elevation.ClearMarker();
            RollBack();
            return "The new version did not start: " + e.Message;
        }
    }

    static void RollBack()
    {
        try
        {
            if (!File.Exists(OldPath)) return;
            File.Delete(ExePath);
            File.Move(OldPath, ExePath);
        }
        catch { }
    }

    // The previous exe stays behind as ShareKM.exe.old until the next start.
    public static void CleanUp()
    {
        try { File.Delete(OldPath); } catch { }
        try { File.Delete(NewPath); } catch { }
    }

    // Opens a web page in the default browser. From an elevated copy, Explorer starts it so the browser is not elevated.
    public static void OpenUrl(string url)
    {
        try
        {
            if (Elevation.IsElevated)
            {
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { url }, UseShellExecute = false })?.Dispose();
            }
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch { }
    }
}
