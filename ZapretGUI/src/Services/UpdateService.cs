using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ZapretGUI.Core;

namespace ZapretGUI.Services;

public sealed record GitHubRelease(string TagName, string Name, DateTime PublishedAt, List<GitHubAsset> Assets);
public sealed record GitHubAsset(string Name, long Size, string DownloadUrl);

/// <summary>
/// Обновление конфигураций и бинарников:
///  - flowseal/zapret-discord-youtube (winws + списки + .bin фейков) — релизы GitHub;
///  - bol-van/byedpi — исходники/релизы GitHub (для Windows собирается в CI zapret, бинарь берём из release assets);
///  - bol-van/zapret — доменные базы list/*.txt (discoid, rutracker, youtube) для пополнения списков.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly AppPaths _paths;
    private readonly LogService _log;

    public event Action<string>? ProgressChanged;
    public event Action<double>? ProgressPercent; // 0..1

    public UpdateService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretGUI", "1.2"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.Timeout = TimeSpan.FromMinutes(5);
        return c;
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(string apiRepoUrl)
    {
        try
        {
            using var resp = await Http.GetAsync(apiRepoUrl);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var rel = new GitHubRelease(
                root.GetProperty("tag_name").GetString() ?? "",
                root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var dt) ? dt : DateTime.MinValue,
                new List<GitHubAsset>());
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    rel.Assets.Add(new GitHubAsset(
                        a.GetProperty("name").GetString() ?? "",
                        a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                        a.GetProperty("browser_download_url").GetString() ?? ""));
            return rel;
        }
        catch (Exception ex)
        {
            _log.Error($"Не удалось получить список релизов {apiRepoUrl}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Проверяет наличие новой версии flowseal относительно LastFlowsealVersion в конфиге.</summary>
    public async Task<(string remote, bool newer)> CheckFlowsealUpdateAsync(GuiConfig cfg)
    {
        var rel = await GetLatestReleaseAsync(AppUrls.FlowsealApiLatest);
        if (rel == null) return ("недоступно", false);
        var local = cfg.LastFlowsealVersion;
        var isNewer = !string.IsNullOrEmpty(rel.TagName) &&
                      (string.IsNullOrEmpty(local) || CompareVersions(rel.TagName, local) > 0);
        return (rel.TagName, isNewer);
    }

    public static int CompareVersions(string a, string b)
    {
        var pa = a.TrimStart('v', 'V').Split('.');
        var pb = b.TrimStart('v', 'V').Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i].Where(char.IsDigit).ToArray(), out var x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i].Where(char.IsDigit).ToArray(), out var y) ? y : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }

    /// <summary>
    /// Полное обновление конфигураций flowseal: скачивает zip-архив последнего релиза,
    /// обновляет bin\*.exe/*.dll/*.sys/*.bin, lists\*.txt (не трогая *-user файлы), service.bat.
    /// Возвращает установленную версию.
    /// </summary>
    public async Task<string> UpdateFlowsealAsync(GuiConfig cfg, CancellationToken ct)
    {
        Report("Получение информации о последней версии flowseal…", 0);
        var rel = await GetLatestReleaseAsync(AppUrls.FlowsealApiLatest)
                  ?? throw new InvalidOperationException("GitHub API недоступен.");
        var zip = rel.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException("В релизе нет zip-архива.");

        var tmp = Path.Combine(Path.GetTempPath(), "zapretgui-update");
        Directory.CreateDirectory(tmp);
        var archivePath = Path.Combine(tmp, zip.Name);

        Report($"Скачивание {zip.Name} ({zip.Size / 1024 / 1024} МБ)…", 0.05);
        await DownloadFileAsync(zip.DownloadUrl, archivePath, 0.05, 0.55, ct);

        Report("Распаковка и установка файлов…", 0.6);
        var staging = Path.Combine(tmp, "extracted");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(archivePath, staging);
        var inner = FindContentRoot(staging);

        _paths.EnsureDirs();
        CopyMatching(inner, "bin", _paths.BinDir, "*.*", overwriteAlways: true);
        // списки: перезаписываем только служебные, user-файлы не трогаем
        // служебные списки ВСЕГДА перезаписываем свежими из релиза (user-файлы *-user.txt не трогаем)
        CopyMatching(inner, "lists", _paths.ListsDir, "list-general.txt", true);
        CopyMatching(inner, "lists", _paths.ListsDir, "list-google.txt", true);
        CopyMatching(inner, "lists", _paths.ListsDir, "list-exclude.txt", true);
        CopyMatching(inner, "lists", _paths.ListsDir, "ipset-all.txt", true);
        CopyMatching(inner, "lists", _paths.ListsDir, "ipset-exclude.txt", true);
        CopyMatching(inner, "", _paths.Root, "service.bat", false);
        CopyMatching(inner, "utils", _paths.UtilsDir, "targets.txt", false);

        // удаляем временный архив
        try { Directory.Delete(tmp, true); } catch { }

        cfg.LastFlowsealVersion = rel.TagName;
        cfg.LastUpdateUtc = DateTime.UtcNow;
        Report($"Установлена версия flowseal {rel.TagName}.", 1.0);
        _log.Info($"Обновление flowseal завершено: {rel.TagName}");
        return rel.TagName;
    }

    /// <summary>
    /// Обновление доменных баз из оригинального zapret (bol-van): добавляет в list-general-user.txt
    /// отсутствующие домены из discoid/rutracker/youtube баз, ничего не удаляя.
    /// </summary>
    public async Task<int> UpdateDomainBasesAsync(GuiConfig cfg, CancellationToken ct)
    {
        Report("Загрузка баз доменов из bol-van/zapret…", 0.1);
        var url = "https://codeload.github.com/bol-van/zapret/tar.gz/refs/heads/master";
        var tmpTar = Path.Combine(Path.GetTempPath(), "zapretgui-zapret-master.tar.gz");
        await DownloadFileAsync(url, tmpTar, 0.1, 0.6, ct);

        Report("Извлечение доменных списков…", 0.7);
        var extractDir = Path.Combine(Path.GetTempPath(), "zapretgui-zapret-src");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);
        ExtractTarGz(tmpTar, extractDir);
        try { File.Delete(tmpTar); } catch { }

        // нужные нам хостбазы zapret: discoid (discord+youtube), rutracker, google
        var wantedFiles = new[] { "discoid", "rutracker", "youtube", "google" };
        var filesDir = Directory.EnumerateDirectories(extractDir, "files")
                               .SelectMany(d => Directory.EnumerateFiles(d))
                               .ToList();
        // В zapref list files лежат в common/list/* или files/fake... Ищем по имени где угодно:
        var listDirs = Directory.EnumerateDirectories(extractDir, "list", SearchOption.AllDirectories)
                                .Concat(Directory.EnumerateDirectories(extractDir, "hostlist", SearchOption.AllDirectories))
                                .ToList();
        var domainLines = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var dir in listDirs)
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (wantedFiles.Any(w => name.Contains(w)))
                    foreach (var line in ReadLinesSmart(f))
                        if (IsDomain(line)) domainLines.Add(line.Trim());
            }

        Report($"Найдено {domainLines.Count} доменов в базах zapret. Слияние с пользовательским списком…", 0.85);

        // Пользовательский merged-список: дополняем list-general-user.txt недостающими важными доменами,
        // но чтобы не раздувать файл — добавляем только ключевые домены блокируемых сервисов.
        var keyDomains = domainLines
            .Where(d => d.Contains("discord") || d.Contains("youtube") || d.Contains("ytimg") ||
                        d.Contains("googlevideo") || d.Contains("rutracker") || d.Contains("discordapp") ||
                        d.Contains("discord.media") || d.Contains("gstatic") || d.Contains("google"))
            .OrderBy(d => d, System.StringComparer.Ordinal)
            .ToList();

        _paths.EnsureDirs();
        var existing = new HashSet<string>(ReadLinesSmart(_paths.ListGeneralUser).Select(s => s.Trim()), System.StringComparer.OrdinalIgnoreCase);
        // также то, что уже есть в основном списке — дубликаты не пишем
        foreach (var l in ReadLinesSmart(_paths.ListGeneral)) existing.Add(l.Trim());

        int added = 0;
        using (var w = new StreamWriter(_paths.ListGeneralUser, append: true))
        {
            w.WriteLine($"# auto-added from bol-van/zapret bases {DateTime.Now:yyyy-MM-dd HH:mm}");
            foreach (var d in keyDomains)
                if (existing.Add(d)) { w.WriteLine(d); added++; }
        }

        cfg.UserDomains = LoadListFile(_paths.ListGeneralUser);
        Report($"Добавлено новых доменов: {added}.", 1.0);
        _log.Info($"Обновление баз доменов: добавлено {added} из {keyDomains.Count} ключевых.");
        return added;
    }

    /// <summary>Скачивание byedpi.exe из релизов bol-van/byedpi (или warning если нет windows ассета).</summary>
    public async Task<string> UpdateByedpiAsync(CancellationToken ct)
    {
        Report("Поиск byedpi в релизах bol-van/byedpi…", 0.1);
        var rel = await GetLatestReleaseAsync(AppUrls.ByedpiApiLatest);
        if (rel == null || rel.Assets.Count == 0)
            throw new InvalidOperationException(
                "У репозитория bol-van/byedpi нет готовых Windows-бинарников в релизах. " +
                "Соберите byedpi.exe через WSL/MinGW или возьмите из сборки zapret (winws эквивалентен).");

        var asset = rel.Assets.FirstOrDefault(a => a.Name.Contains("win", StringComparison.OrdinalIgnoreCase))
                    ?? rel.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ?? rel.Assets.First();

        var tmp = Path.Combine(Path.GetTempPath(), "zapretgui-byedpi");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, asset.Name);
        await DownloadFileAsync(asset.DownloadUrl, path, 0.2, 0.7, ct);

        _paths.EnsureDirs();
        if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var ext = Path.Combine(tmp, "x");
            if (Directory.Exists(ext)) Directory.Delete(ext, true);
            ZipFile.ExtractToDirectory(path, ext);
            // в релизах bol-van/byedpi для Windows бинарник называется ciadpi.exe
            var exe = Directory.EnumerateFiles(ext, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f => { var n = Path.GetFileName(f).ToLowerInvariant(); return n.Contains("byedpi") || n.Contains("ciadpi"); });
            if (exe == null) throw new InvalidOperationException("byedpi.exe/ciadpi.exe не найден в архиве.");
            File.Copy(exe, _paths.ByedpiExe, overwrite: true);
        }
        else if (asset.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var inF = File.OpenRead(path);
            using var gz = new GZipStream(inF, CompressionMode.Decompress);
            using var outF = File.Create(_paths.ByedpiExe);
            await gz.CopyToAsync(outF, ct);
        }
        else
        {
            File.Copy(path, _paths.ByedpiExe, overwrite: true);
        }

        try { Directory.Delete(tmp, true); } catch { }
        Report($"byedpi {rel.TagName} установлен.", 1.0);
        return rel.TagName;
    }

    // ================= helpers =================

    private void Report(string msg, double? percent)
    {
        ProgressChanged?.Invoke(msg);
        if (percent.HasValue) ProgressPercent?.Invoke(percent.Value);
        _log.Info(msg);
    }

    public async Task DownloadFileAsync(string url, string dest, double fromPct, double toPct, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long done = 0; int read;
        var lastReport = DateTime.MinValue;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (DateTime.Now - lastReport > TimeSpan.FromMilliseconds(200))
            {
                lastReport = DateTime.Now;
                var frac = total > 0 ? (double)done / total : 0;
                ProgressPercent?.Invoke(fromPct + frac * (toPct - fromPct));
                ProgressChanged?.Invoke($"Загрузка… {done / 1024 / 1024} МБ{(total > 0 ? $" из {total / 1024 / 1024} МБ" : "")}");
            }
        }
    }

    private static string FindContentRoot(string extracted)
    {
        // архив flowseal содержит папку zapret-discord-youtube-x.y.z/ либо сразу bin/lists
        if (Directory.Exists(Path.Combine(extracted, "bin"))) return extracted;
        foreach (var d in Directory.GetDirectories(extracted))
            if (Directory.Exists(Path.Combine(d, "bin"))) return d;
        return extracted;
    }

    private static void CopyMatching(string srcRoot, string subSrc, string dstDir, string pattern, bool overwriteAlways)
    {
        var srcDir = string.IsNullOrEmpty(subSrc) ? srcRoot : Path.Combine(srcRoot, subSrc);
        if (!Directory.Exists(srcDir)) return;
        Directory.CreateDirectory(dstDir);
        foreach (var f in Directory.GetFiles(srcDir, pattern))
        {
            var dst = Path.Combine(dstDir, Path.GetFileName(f));
            if (overwriteAlways || !File.Exists(dst))
                File.Copy(f, dst, overwrite: true);
        }
    }

    private static IEnumerable<string> ReadLinesSmart(string file)
    {
        if (!File.Exists(file)) return Array.Empty<string>();
        try { return File.ReadAllLines(file); }
        catch { return Array.Empty<string>(); }
    }

    private static bool IsDomain(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s.StartsWith('#')) return false;
        if (s.Contains('/')) return false;
        return s.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '*');
    }

    public static List<string> LoadListFile(string path)
        => ReadLinesSmart(path).Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')).ToList();

    private static void ExtractTarGz(string tarGzPath, string outDir)
    {
        using var fs = File.OpenRead(tarGzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        ExtractTar(gz, outDir);
    }

    /// <summary>Минимальный USTAR-парсер tar (GNU longname поддержан).</summary>
    private static void ExtractTar(Stream stream, string outDir)
    {
        var header = new byte[512];
        string? longName = null;
        while (true)
        {
            int read = ReadExact(stream, header, 0, 512);
            if (read < 512) break;
            if (header.All(b => b == 0)) break;

            string name = DecodeString(header, 0, 100);
            long size = ParseOctal(header, 124, 12);
            byte typeFlag = header[156];
            string prefix = DecodeString(header, 345, 155);

            if (name == "././@LongLink" || name == "@LongLink")
            {
                var buf = new byte[size];
                ReadExact(stream, buf, 0, (int)size);
                longName = DecodeBytes(buf);
                SkipPadding(stream, size);
                continue;
            }

            if (!string.IsNullOrEmpty(longName)) { name = longName; longName = null; }
            if (!string.IsNullOrEmpty(prefix)) name = prefix + "/" + name;
            var target = Path.GetFullPath(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.GetFullPath(outDir), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Небезопасный путь в tar: " + name);

            if (typeFlag is (byte)'5' or (byte)'D')
                Directory.CreateDirectory(target);
            else if (typeFlag is (byte)'0' or (byte)'\0' or (byte)'7')
            {
                var dir = Path.GetDirectoryName(target);
                if (dir != null) Directory.CreateDirectory(dir);
                using var outFs = File.Create(target);
                CopyStreamLimited(stream, outFs, size);
            }
            else
                SkipStream(stream, size);

            SkipPadding(stream, size);
        }
    }

    private static int ReadExact(Stream s, byte[] buf, int off, int len)
    {
        int total = 0;
        while (total < len)
        {
            int r = s.Read(buf, off + total, len - total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }

    private static void CopyStreamLimited(Stream src, Stream dst, long size)
    {
        var buf = new byte[81920];
        long left = size;
        while (left > 0)
        {
            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (r <= 0) break;
            dst.Write(buf, 0, r);
            left -= r;
        }
    }

    private static void SkipStream(Stream s, long size)
    {
        var buf = new byte[81920];
        long left = size;
        while (left > 0)
        {
            int r = s.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (r <= 0) break;
            left -= r;
        }
    }

    private static void SkipPadding(Stream s, long size)
    {
        long rem = size % 512;
        if (rem != 0) SkipStream(s, 512 - rem);
    }

    private static long ParseOctal(byte[] h, int off, int len)
    {
        long v = 0;
        for (int i = 0; i < len; i++)
        {
            byte b = h[off + i];
            if (b >= '0' && b <= '7') v = (v << 3) + (b - '0');
            else if (v != 0) break;
        }
        return v;
    }

    private static string DecodeString(byte[] h, int off, int len) => DecodeBytes(h.AsSpan(off, len).TrimEnd(stackalloc byte[] { 0, 32 }));
    private static string DecodeBytes(ReadOnlySpan<byte> b) => System.Text.Encoding.UTF8.GetString(b.ToArray());

    private sealed class StringComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(string s) => s.ToLowerInvariant().GetHashCode();
    }
}
