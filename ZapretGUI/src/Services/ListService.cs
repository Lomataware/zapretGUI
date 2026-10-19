using System.IO;
using ZapretGUI.Core;

namespace ZapretGUI.Services;

/// <summary>Чтение/запись файлов списков flowseal (lists\*.txt). User-файлы не перезаписываются при обновлении.</summary>
public sealed class ListService
{
    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private readonly LogService _log;

    public ListService(AppPaths paths, ConfigService config, LogService log)
    {
        _paths = paths; _config = config; _log = log;
    }

    public List<string> LoadDomains() => UpdateService.LoadListFile(_paths.ListGeneralUser);
    public List<string> LoadExcludes() => UpdateService.LoadListFile(_paths.ListExcludeUser);
    public List<string> LoadIpset() => UpdateService.LoadListFile(IpsetUserPath());
    public List<string> LoadIpsetExcludes() => UpdateService.LoadListFile(_paths.IpsetExcludeUser);

    public string IpsetUserPath() => Path.Combine(_paths.ListsDir, "ipset-user.txt");

    public void SaveDomains(IEnumerable<string> lines)
    {
        var clean = Normalize(lines, isDomain: true);
        WriteList(_paths.ListGeneralUser, clean);
        _config.Config.UserDomains = clean;
        _config.Save();
        _log.Info($"Сохранён пользовательский список доменов: {clean.Count} строк.");
    }

    public void SaveExcludes(IEnumerable<string> lines)
    {
        var clean = Normalize(lines, isDomain: true);
        WriteList(_paths.ListExcludeUser, clean);
        _config.Config.UserExcludes = clean;
        _config.Save();
    }

    public void SaveIpset(IEnumerable<string> lines)
    {
        var clean = Normalize(lines, isDomain: false);
        WriteList(IpsetUserPath(), clean);
        // winws в сборке flowseal читает user ipset только если добавлен --ipset=path;
        // пробрасываем его через general профиль автоматически (см. ProfileBuilder HostlistArgs)
        _config.Config.UserIpset = clean;
        _config.Save();
    }

    public void SaveIpsetExcludes(IEnumerable<string> lines)
    {
        var clean = Normalize(lines, isDomain: false);
        WriteList(_paths.IpsetExcludeUser, clean);
        _config.Config.UserIpsetExcludes = clean;
        _config.Save();
    }

    private static IEnumerable<string> Normalize(IEnumerable<string> lines, bool isDomain)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith('#')) continue;
            if (isDomain)
            {
                s = s.ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\w.*\-]+$")) continue;
            }
            else
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9a-fA-F:.]+(/\d+)?$")) continue;
            }
            set.Add(s);
        }
        return set.OrderBy(x => x, StringComparer.Ordinal);
    }

    private static void WriteList(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    /// <summary>Счётчики для UI: размеры основных баз.</summary>
    public (int general, int google, int ipset, int userD, int userE) GetCounts()
    {
        return (
            Count(_paths.ListGeneral),
            Count(_paths.ListGoogle),
            Count(_paths.IpsetAll),
            Count(_paths.ListGeneralUser),
            Count(_paths.ListExcludeUser));
    }

    private static int Count(string p) => File.Exists(p) ? UpdateService.LoadListFile(p).Count : 0;
}
