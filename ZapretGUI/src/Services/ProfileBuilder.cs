using System.Diagnostics;
using System.IO;
using System.Text;
using ZapretGUI.Core;

namespace ZapretGUI.Services;

/// <summary>
/// Генерация командной строки winws.exe (zapret) по конфигурации GUI.
/// Структура правил повторяет general.bat / ALT-профили flowseal, но собирается динамически.
/// </summary>
public sealed class WinwsProfileBuilder
{
    private readonly AppPaths _paths;
    public WinwsProfileBuilder(AppPaths paths) => _paths = paths;

    /// <summary>Возвращает аргументы командной строки для winws.exe.</summary>
    public string BuildArgs(GuiConfig cfg)
    {
        if (cfg.StrategyId == "custom" && File.Exists(cfg.CustomProfilePath))
            return BuildFromCustomProfile(cfg);

        var bin = EnsureTrailingSlash(_paths.BinDir);
        var lst = EnsureTrailingSlash(_paths.ListsDir);
        var rules = new List<string>();

        string tcpPorts = cfg.FilterTCPPorts;
        string udpPorts = cfg.FilterUDPPorts;
        if (cfg.GameFilterEnabled && !string.IsNullOrWhiteSpace(cfg.GameFilterTCP))
            tcpPorts += "," + cfg.GameFilterTCP.Trim();
        if (cfg.GameFilterEnabled && !string.IsNullOrWhiteSpace(cfg.GameFilterUDP))
            udpPorts += "," + cfg.GameFilterUDP.Trim();

        string userIpsetArg = File.Exists($"{lst}ipset-user.txt") ? $" --ipset=\"{lst}ipset-user.txt\"" : "";

        // ---- Стратегии: НЕ мутируем cfg — только локальные параметры режима desync ----
        int seqovlHttp = cfg.SeqovlHttp, seqovlTls = cfg.SeqovlTls;
        bool useFakeTls = cfg.FakeTlsEnabled, splitOnly = false, useFakedsplit = false;
        switch (cfg.StrategyId)
        {
            case "general": break;                            // базовый multisplit
            case "alt":  seqovlHttp = 650; seqovlTls = 681; break; // как general (ALT).bat
            case "alt2": useFakedsplit = true; break;         // fakedsplit для HTTPS
            case "alt3": splitOnly = true; break;             // split2 без seqovl — мягкий
            case "alt4": break;                               // комбинированный: правила ниже уже содержат fake+multisplit
            case "faketls":
            case "simplefake": useFakeTls = true; break;      // FAKE TLS AUTO / Simple Fake
            case "alt5": break;                               // autohostlist — отдельное правило ниже
        }

        var global = new StringBuilder();
        if (cfg.FilterTCPEnabled) global.Append($"--wf-tcp={tcpPorts} ");
        if (cfg.FilterUDPEnabled) global.Append($"--wf-udp={udpPorts} ");

        // ---- Правило 1: QUIC (UDP 443) по hostlist — fake ----
        if (cfg.FakeQuicEnabled && cfg.FilterUDPEnabled)
        {
            rules.Add(
                $"--filter-udp=443 " +
                HostlistArgs(lst, cfg) +
                ExcludeArgs(lst, cfg) +
                $"--dpi-desync=fake --dpi-desync-repeats={cfg.FakeRepeats} " +
                $"--dpi-desync-fake-quic=\"{bin}quic_initial_www_google_com.bin\"");
        }

        // ---- Правило 2: Discord voice UDP (STUN) — fake discord/stun ----
        if (cfg.UdpDiscordFake && cfg.FilterUDPEnabled)
        {
            string udpVoice = ExtractVoicePorts(cfg.FilterUDPPorts);
            if (!string.IsNullOrEmpty(udpVoice))
            {
                rules.Add(
                    $"--filter-udp={udpVoice} --filter-l7=discord,stun " +
                    $"--dpi-desync=fake --dpi-desync-repeats={cfg.FakeRepeats} " +
                    $"--dpi-desync-fake-discord=\"{bin}ACTIVE_DISCORD_UDP.bin\" " +
                    $"--dpi-desync-fake-stun=\"{bin}ACTIVE_DISCORD_UDP.bin\"");
            }
        }

        // ---- Правило 3: discord.media TCP (видео/стримы) — multisplit seqovl=681 ----
        if (cfg.DiscordMediaMultisplit && cfg.DesyncEnabled && cfg.FilterTCPEnabled)
        {
            rules.Add(
                $"--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media " +
                DesyncTcp(bin, cfg, seqovlTls, useFakeTls, splitOnly, useFakedsplit));
        }

        // ---- Правило 4: google list + ip-id=zero ----
        if (cfg.GoogleZeroIpset && cfg.DesyncEnabled && cfg.FilterTCPEnabled)
        {
            rules.Add(
                $"--filter-tcp=443 --hostlist=\"{lst}list-google.txt\" --ip-id=zero " +
                DesyncTcp(bin, cfg, seqovlTls, useFakeTls, splitOnly, useFakedsplit));
        }


        // ---- Правило 5: основной HTTP(S) по hostlist general (+user) ----
        if (cfg.DesyncEnabled && cfg.FilterTCPEnabled)
        {
            rules.Add(
                $"--filter-tcp=80,443 " +
                HostlistArgs(lst, cfg) +
                ExcludeArgs(lst, cfg) +
                DesyncTcp(bin, cfg, seqovlHttp, useFakeTls, splitOnly, useFakedsplit));
        }

        // ---- Правила QUIC/TLS по IP-базе (ipset-all) ----
        if (cfg.IpsetEnabled)
        {
            if (cfg.FakeQuicEnabled && cfg.FilterUDPEnabled)
            {
                rules.Add(
                    $"--filter-udp=443 --ipset=\"{lst}ipset-all.txt\"" + userIpsetArg + " " +
                    ExcludeArgs(lst, cfg) +
                    $"--dpi-desync=fake --dpi-desync-repeats={cfg.FakeRepeats} " +
                    $"--dpi-desync-fake-quic=\"{bin}quic_initial_www_google_com.bin\"");
            }
            if (cfg.DesyncEnabled && cfg.FilterTCPEnabled)
            {
                rules.Add(
                    $"--filter-tcp=80,443,8443 --ipset=\"{lst}ipset-all.txt\"" + userIpsetArg + " " +
                    ExcludeArgs(lst, cfg) +
                    DesyncTcp(bin, cfg, seqovlHttp, useFakeTls, splitOnly, useFakedsplit));
            }
        }

        // ---- Игровые порты (game filter) ----
        if (cfg.GameFilterEnabled)
        {
            if (cfg.FilterTCPEnabled && !string.IsNullOrWhiteSpace(cfg.GameFilterTCP) && cfg.DesyncEnabled)
            {
                rules.Add(
                    $"--filter-tcp={cfg.GameFilterTCP.Trim()} --ipset=\"{lst}ipset-all.txt\" " +
                    $"--ipset-exclude=\"{lst}ipset-exclude.txt\" --ipset-exclude=\"{lst}ipset-exclude-user.txt\" " +
                    $"--dpi-desync=multisplit --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3 " +
                    $"--dpi-desync-split-seqovl={cfg.SeqovlHttp} --dpi-desync-split-pos={cfg.SplitPosition} " +
                    $"--dpi-desync-split-seqovl-pattern=\"{bin}tls_clienthello_4pda_to.bin\"");
            }
            if (cfg.FilterUDPEnabled && !string.IsNullOrWhiteSpace(cfg.GameFilterUDP))
            {
                rules.Add(
                    $"--filter-udp={cfg.GameFilterUDP.Trim()} --ipset=\"{lst}ipset-all.txt\" " +
                    $"--ipset-exclude=\"{lst}ipset-exclude.txt\" --ipset-exclude=\"{lst}ipset-exclude-user.txt\" " +
                    $"--dpi-desync=fake --dpi-desync-repeats=12 --dpi-desync-any-protocol=1 " +
                    $"--dpi-desync-fake-unknown-udp=\"{bin}ACTIVE_GAME_UDP.bin\" --dpi-desync-cutoff=n2");
            }
        }

        // ---- FAKE TLS режим вместо multisplit (как general (FAKE TLS AUTO).bat) ----
        if (useFakeTls)
        {
            rules.RemoveAll(r => r.Contains("--dpi-desync=multisplit"));
            rules.Add(
                $"--filter-tcp=80,443 " + HostlistArgs(lst, cfg) + ExcludeArgs(lst, cfg) +
                $"--dpi-desync=fake --dpi-desync-ttl=4 --dpi-desync-repeats=6 " +
                $"--dpi-desync-fake-tls=\"{bin}tls_clienthello_www_google_com.bin\"");
        }

        // ---- AUTOHOSTLIST (ALT5) — движок сам добавляет домены по таймаутам ----
        if (cfg.StrategyId == "alt5")
        {
            rules.Add(
                $"--filter-tcp=80,443 --autohostlist=\"{lst}list-auto\" " +
                $"--dpi-desync=fake --dpi-desync-ttl=4 --dpi-desync-repeats={cfg.FakeRepeats} " +
                $"--dpi-desync-fake-tls=\"{bin}tls_clienthello_www_google_com.bin\"");
        }

        if (rules.Count == 0)
            throw new InvalidOperationException("Не включено ни одного правила. Включите хотя бы одно на вкладке «Движок».");

        return global.ToString().TrimEnd() + " --new " + string.Join(" --new ", rules);
    }

    private string DesyncTcp(string bin, GuiConfig cfg, int seqovl, bool fakeTls, bool splitOnly, bool fakedsplit)
    {
        if (fakeTls)
            return $"--dpi-desync=fake --dpi-desync-ttl=4 --dpi-desync-repeats={cfg.FakeRepeats} " +
                   $"--dpi-desync-fake-tls=\"{bin}tls_clienthello_www_google_com.bin\"";
        if (fakedsplit)
            return "--dpi-desync=fakedsplit --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n2";
        if (splitOnly)
            return $"--dpi-desync=split2 --dpi-desync-split-pos={cfg.SplitPosition} --dpi-desync-any-protocol=1";
        return $"--dpi-desync=multisplit --dpi-desync-split-seqovl={seqovl} " +
               $"--dpi-desync-split-pos={cfg.SplitPosition} " +
               $"--dpi-desync-split-seqovl-pattern=\"{bin}tls_clienthello_www_google_com.bin\"";
    }

    private string HostlistArgs(string lst, GuiConfig cfg)
    {
        var sb = new StringBuilder();
        sb.Append($"--hostlist=\"{lst}list-general.txt\" ");
        if (File.Exists($"{lst}list-general-user.txt"))
            sb.Append($"--hostlist=\"{lst}list-general-user.txt\" ");
        return sb.ToString();
    }

    private string ExcludeArgs(string lst, GuiConfig cfg)
    {
        var sb = new StringBuilder();
        if (File.Exists($"{lst}list-exclude.txt")) sb.Append($"--hostlist-exclude=\"{lst}list-exclude.txt\" ");
        if (File.Exists($"{lst}list-exclude-user.txt")) sb.Append($"--hostlist-exclude=\"{lst}list-exclude-user.txt\" ");
        if (File.Exists($"{lst}ipset-exclude.txt")) sb.Append($"--ipset-exclude=\"{lst}ipset-exclude.txt\" ");
        if (File.Exists($"{lst}ipset-exclude-user.txt")) sb.Append($"--ipset-exclude=\"{lst}ipset-exclude-user.txt\" ");
        return sb.ToString();
    }

    private static string ExtractVoicePorts(string udpPorts)
    {
        // извлекаем всё кроме 443 (voice диапазоны)
        var parts = udpPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(p => p != "443").ToList();
        return string.Join(",", parts);
    }

    private string BuildFromCustomProfile(GuiConfig cfg)
    {
        var bin = EnsureTrailingSlash(_paths.BinDir);
        var lst = EnsureTrailingSlash(_paths.ListsDir);
        var raw = File.ReadAllLines(cfg.CustomProfilePath);
        // склеиваем bat-продолжения строк через ^, затем берём ВСЮ команду запуска winws целиком
        var logical = new List<string>();
        var sbLine = new StringBuilder();
        foreach (var line in raw)
        {
            var t = line.Trim();
            if (t.EndsWith("^")) { sbLine.Append(t[..^1]).Append(' '); continue; }
            sbLine.Append(t);
            logical.Add(sbLine.ToString());
            sbLine.Clear();
        }
        if (sbLine.Length > 0) logical.Add(sbLine.ToString());

        var cmdLine = logical.FirstOrDefault(l => l.Contains("winws", StringComparison.OrdinalIgnoreCase));
        if (cmdLine == null)
            throw new InvalidOperationException("В пользовательском профиле не найдена команда запуска winws.exe");

        // разворачиваем переменные flowseal .bat профилей (%LISTS%, %BIN%, %~dp0...)
        cmdLine = cmdLine.Replace("%LISTS%", lst).Replace("%list%", lst).Replace("%LISTDIR%", lst)
                         .Replace("%BIN%", bin).Replace("%bin%", bin)
                         .Replace("%~dp0lists\\", lst).Replace("%~dp0bin\\", bin)
                         .Replace("%~dp0", _paths.Root + "\\");
        // вырезаем аргументы: всё после имени exe (в кавычках или без)
        var m = System.Text.RegularExpressions.Regex.Match(cmdLine,
            @"[^\s]*winws\.exe[^\s]*\s*(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new InvalidOperationException("Не удалось разобрать команду запуска winws.exe в профиле.");
        var args = m.Groups[1].Value.Trim();
        args = System.Text.RegularExpressions.Regex.Replace(args, @"\s+", " ");
        return args;
    }

    private static string EnsureTrailingSlash(string s) => s.EndsWith('\\') ? s : s + "\\";
}

/// <summary>Генерация аргументов byedpi.exe (bol-van/byedpi).</summary>
public sealed class ByedpiProfileBuilder
{
    public string BuildArgs(GuiConfig cfg)
    {
        var sb = new StringBuilder();
        // Корректные ключи byedpi/ciadpi (github.com/bol-van/byedpi):
        // -p <port> HTTP(S)-порт прокси; -s включает SOCKS5 на том же порту (флаг без значения!)
        sb.Append($"-p{cfg.ByedpiHttpPort} ");
        if (cfg.ByedpiSocksEnabled) sb.Append("-s ");
        // фрагментация: -f <modes> (bitmask: 1=TCP_HTTP, 2=TLS1.2...); "-fa" нет — используем числовой режим
        if (cfg.ByedpiFragAll) sb.Append("-f7 ");       // все основные L7 протоколы
        else sb.Append("-f2 ");                          // только TLS1.2
        // disorder: -o <modes>:<packetno>
        if (cfg.ByedpiDisorder) sb.Append("-o7:1 ");
        // find-only: -1 (only detect, no modification)
        if (cfg.ByedpiFind) sb.Append("-1 ");
        // EOF-замена последних байтов: -Q <mode>
        if (cfg.ByedpiEofReplace) sb.Append("-Q7 ");
        // TTL для фрагментов/split: -m <ttl>
        if (cfg.ByedpiTtlSplit > 0) sb.Append($"-m{cfg.ByedpiTtlSplit} ");
        // redirect/keepalive модификации: -n no keepalive alive-from-dpi? (byedpi: -N disallow keepalive)
        if (cfg.ByedpiNoFollowRedirects) sb.Append("-N ");
        // wisp-like mode: -W
        sb.Append("-d1");                                // уровень логов в stdout
        return sb.ToString().Trim();
    }
}
