using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PopBar.Plugins;

/// <summary>
/// Fetches third-party plugins into <see cref="PluginStorage.PluginsDir"/>: from a GitHub repository's latest release
/// (a .zip or .dll asset) or from a .zip / .dll the user picks. Preparing (download, unpack, check) happens in a temp
/// folder; <see cref="Commit"/> then swaps it into place. Plugin DLLs are loaded from memory
/// (<see cref="PluginManager"/>), so nothing is locked and a running plugin's folder can be replaced or deleted.
/// </summary>
internal static partial class PluginInstaller
{
    /// <summary>A plugin unpacked and checked in <see cref="TempDir"/>, to be installed as <see cref="Name"/>.</summary>
    public sealed record Prepared(string Name, string TempDir, string Source, string Root) : IDisposable
    {
        public void Dispose() => TryDeleteDir(Root);
    }

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?github\.com/(?<owner>[\w.-]+)/(?<repo>[\w.-]+?)(?:\.git)?(?:/.*)?$|^(?<owner>[\w.-]+)/(?<repo>[\w.-]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RepoRegex();

    /// <summary>"owner/repo" from a repository URL (any page of it) or the short form; null when it is neither.</summary>
    internal static (string Owner, string Repo)? ParseRepo(string input)
    {
        var m = RepoRegex().Match(input.Trim().TrimEnd('/'));
        return m.Success ? (m.Groups["owner"].Value, m.Groups["repo"].Value) : null;
    }

    /// <summary>Download the latest release of a GitHub repository and prepare its plugin asset.</summary>
    public static async Task<Prepared> FromGitHubAsync(HttpClient http, string repoInput, IProgress<string>? progress, CancellationToken ct = default)
    {
        var parsed = ParseRepo(repoInput) ?? throw new InvalidOperationException("不是 GitHub 仓库地址，应为 https://github.com/作者/仓库 或 作者/仓库");
        var (owner, repo) = parsed;
        progress?.Report($"查询 {owner}/{repo} 的最新发布…");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("找不到这个仓库的发布（Release）。插件需要作者在 Releases 里提供 .zip 或 .dll。");
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub 返回 {(int)resp.StatusCode} {resp.ReasonPhrase}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var assets = doc.RootElement.TryGetProperty("assets", out var a)
            ? a.EnumerateArray().Select(x => (Name: x.GetProperty("name").GetString() ?? "", Url: x.GetProperty("browser_download_url").GetString() ?? "")).ToList()
            : new();
        // A zip named like a plugin first, then any zip, then a bare DLL.
        var asset = assets.Where(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(x => x.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (asset.Url == null) asset = assets.FirstOrDefault(x => x.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (asset.Url == null) throw new InvalidOperationException($"最新发布 {tag} 里没有 .zip 或 .dll 文件。");

        progress?.Report($"下载 {asset.Name}（{tag}）…");
        var tmp = Path.Combine(Path.GetTempPath(), "popbar-plugin-" + Guid.NewGuid().ToString("N") + Path.GetExtension(asset.Name));
        try
        {
            await using (var src = await http.GetStreamAsync(asset.Url, ct))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);
            return Prepare(tmp, repo, $"github.com/{owner}/{repo} {tag}");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>Prepare a .zip (the plugin and its dependencies) or a single plugin .dll.</summary>
    public static Prepared FromFile(string path) => Prepare(path, Path.GetFileNameWithoutExtension(path), path);

    private static Prepared Prepare(string file, string suggestedName, string source)
    {
        var root = Path.Combine(Path.GetTempPath(), "popbar-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var work = root;
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(file, work); // rejects entries that would land outside the folder
                work = Flatten(work);
            }
            else if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // The DLL and its own companions (deps / pdb); anything else it needs has to come as a zip.
                var stem = Path.GetFileNameWithoutExtension(file);
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(file))!, stem + ".*"))
                    if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
                        File.Copy(f, Path.Combine(work, Path.GetFileName(f)));
            }
            else throw new InvalidOperationException("只支持 .zip 或 .dll 文件。");

            // The host's own SDK is always used; a packaged copy would only be loaded and scanned for nothing.
            foreach (var sdk in Directory.GetFiles(work, "PopBar.Sdk.*")) File.Delete(sdk);
            if (!Directory.GetFiles(work, "*.dll").Any(ReferencesSdk))
                throw new InvalidOperationException("里面没有 PopBar 插件（没有引用 PopBar.Sdk 的 DLL）。");

            return new Prepared(FolderName(suggestedName), work, source, root);
        }
        catch
        {
            TryDeleteDir(root);
            throw;
        }
    }

    /// <summary>Where a prepared plugin goes. Unload the plugins loaded from there before <see cref="Commit"/>.</summary>
    public static string TargetOf(Prepared p) => Path.Combine(PluginStorage.PluginsDir, p.Name);

    /// <summary>Replace the plugin's folder with the prepared files; returns the folder.</summary>
    public static string Commit(Prepared p)
    {
        var target = TargetOf(p);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        CopyDir(p.TempDir, target);
        return target;
    }

    /// <summary>Delete an installed plugin's folder (after its plugins were unloaded).</summary>
    public static void Remove(string folder)
    {
        var full = Path.GetFullPath(folder);
        // Never anything but a direct subfolder of the plugins folder.
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(PluginStorage.PluginsDir), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不是插件目录：" + folder);
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }

    /// <summary>Archives often hold one top folder ("MyPlugin-1.0/"): install its contents, not the folder.</summary>
    private static string Flatten(string dir)
    {
        while (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir) is [var only]) dir = only;
        return dir;
    }

    /// <summary>A folder name for the plugin that does not collide with a built-in plugin's data folder.</summary>
    private static string FolderName(string suggested)
    {
        var name = Regex.Replace(suggested.Trim().ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-', '.');
        if (name.Length == 0) name = "plugin";
        var path = Path.Combine(PluginStorage.PluginsDir, name);
        // An existing folder without DLLs is some plugin's data (e.g. plugins\ocr\), not an installed plugin.
        if (Directory.Exists(path) && Directory.GetFiles(path, "*.dll").Length == 0) name += "-plugin";
        return name;
    }

    /// <summary>Is this a managed assembly that references PopBar.Sdk? Read from metadata only, nothing is loaded.</summary>
    internal static bool ReferencesSdk(string dll)
    {
        try
        {
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            return md.AssemblyReferences.Any(h => md.GetString(md.GetAssemblyReference(h).Name) == "PopBar.Sdk");
        }
        catch { return false; } // not a PE file / corrupt: not a plugin
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { Log.Warn($"delete {file}: {ex.Message}"); }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) { Log.Warn($"delete {dir}: {ex.Message}"); }
    }
}
