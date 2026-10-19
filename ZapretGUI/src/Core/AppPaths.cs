using System.IO;

namespace ZapretGUI.Core;

/// <summary>Ключевые адреса GitHub, откуда берётся всё необходимое.</summary>
public static class AppUrls
{
    /// <summary>Репозиторий flowseal (zapret-discord-youtube) — сборки winws + списки.</summary>
    public const string FlowsealRepo = "flowseal/zapret-discord-youtube";
    public const string FlowsealApiLatest = "https://api.github.com/repos/flowseal/zapret-discord-youtube/releases/latest";

    /// <summary>Оригинальный zapret (bol-van) — для обновления list/*.txt баз доменов (discoid, youtube, rutracker).</summary>
    public const string ZapretRepo = "bol-van/zapret";
    public const string ZapretApiLatest = "https://api.github.com/repos/bol-van/zapret/releases/latest";

    /// <summary>byedpi — лёгкий DPI-обходчик (nyxmaiden/tpproxy наследие), репозиторий bol-van/byedpi.</summary>
    public const string ByedpiRepo = "bol-van/byedpi";
    public const string ByedpiApiLatest = "https://api.github.com/repos/bol-van/byedpi/releases/latest";

    public const string FlowsealReleasesPage = "https://github.com/flowseal/zapret-discord-youtube/releases";
    public const string ZapretDocsFaq = "https://github.com/bol-van/zapret/blob/master/docs/readme.md#configuration";
}

/// <summary>Все пути программы. По умолчанию — рядом с exe (портативный режим), как у flowseal.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string BinDir => Path.Combine(Root, "bin");
    public string ListsDir => Path.Combine(Root, "lists");
    public string UtilsDir => Path.Combine(Root, "utils");
    public string LogsDir => Path.Combine(Root, "logs");

    public string WinwsExe => Path.Combine(BinDir, "winws.exe");
    public string ByedpiExe => Path.Combine(BinDir, "byedpi.exe");
    public string ServiceBat => Path.Combine(Root, "service.bat");

    public string ListGeneral => Path.Combine(ListsDir, "list-general.txt");
    public string ListGeneralUser => Path.Combine(ListsDir, "list-general-user.txt");
    public string ListGoogle => Path.Combine(ListsDir, "list-google.txt");
    public string ListExclude => Path.Combine(ListsDir, "list-exclude.txt");
    public string ListExcludeUser => Path.Combine(ListsDir, "list-exclude-user.txt");
    public string IpsetAll => Path.Combine(ListsDir, "ipset-all.txt");
    public string IpsetExclude => Path.Combine(ListsDir, "ipset-exclude.txt");
    public string IpsetExcludeUser => Path.Combine(ListsDir, "ipset-exclude-user.txt");

    public string GameDiscordUdpBin => Path.Combine(BinDir, "ACTIVE_DISCORD_UDP.bin");
    public string GameFilterFile => Path.Combine(UtilsDir, "gamefilter.txt");
    public string UpdateMarkerFile => Path.Combine(UtilsDir, "check_updates.enabled");
    public string StateFile => Path.Combine(UtilsDir, "gui-state.json");

    public static AppPaths CreateDefault()
    {
        var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        // Если рядом есть bin\winws.exe или lists\ — считаем корнем эту папку, иначе подпапку zapret
        if (File.Exists(Path.Combine(baseDir, "bin", "winws.exe")) ||
            Directory.Exists(Path.Combine(baseDir, "lists")))
            return new AppPaths(baseDir);
        var sub = Path.Combine(baseDir, "zapret");
        if (File.Exists(Path.Combine(sub, "bin", "winws.exe")))
            return new AppPaths(sub);
        return new AppPaths(baseDir);
    }

    private AppPaths(string root) => Root = Path.GetFullPath(root);

    public void EnsureDirs()
    {
        Directory.CreateDirectory(ListsDir);
        Directory.CreateDirectory(UtilsDir);
        Directory.CreateDirectory(LogsDir);
    }
}
