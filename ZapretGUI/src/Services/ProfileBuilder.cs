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
                DesyncTcp(bin, cfg, cfg.SeqovlTls));
        }

        // ---- Правило 4: google list + ip-id=zero ----
        if (cfg.GoogleZeroIpset && cfg.DesyncEnabled && cfg.FilterTCPEnabled)
        {
            rules.Add(
                $"--filter-tcp=443 --hostlist=\"{lst}list-google.txt\" --ip-id=zero " +
                DesyncTcp(bin, cfg, cfg.SeqovlTls));
        }

        // ---- Правило 5: основной HTTP(S) по hostlist general (+user) ----
        if (cfg.DesyncEnabled && cfg.FilterTCPEnabled)
        {
            rules.Add(
                $"--filter-tcp=80,443 " +
                HostlistArgs(lst, cfg) +
                ExcludeArgs(lst, cfg) +
                DesyncTcp(bin, cfg, cfg.SeqovlHttp));
        }

        // ---- Правила QUIC/TLS по IP-базе (ipset-all) ----
        if (cfg.IpsetEnabled)
        {
            if (cfg.FakeQuicEnabled && cfg.FilterUDPEnabled)
            {
                rules.Add(
                    $"--filter-udp=443 --ipset=\"{lst}ipset-all.txt\" " +
                    ExcludeArgs(lst, cfg) +
                    $"--dpi-desync=fake --dpi-desync-repeats={cfg.FakeRepeats} " +
                    $"--dpi-desync-fake-quic=\"{bin}quic_initial_www_google_com.bin\"");
            }
            if (cfg.DesyncEnabled && cfg.FilterTCPEnabled)
            {
                rules.Add(
                    $"--filter-tcp=80,443,8443 --ipset=\"{lst}ipset-all.txt\" " +
                    ExcludeArgs(lst, cfg) +
                    DesyncTcp(bin, cfg, cfg.SeqovlHttp));
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
        if (cfg.FakeTlsEnabled)
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
                $"--filter-tcp=80,443 --auto-hostlist --hostlist=\"%LISTS%list-auto.txt\" " +
                DesyncTcp(bin, cfg, cfg.SeqovlHttp));
        }

        if (rules.Count == 0)
            throw new InvalidOperationException("Не включено ни одного правила. Включите хотя бы одно на вкладке «Движок».");

        return global.ToString().TrimEnd() + " --new " + string.Join(" --new ", rules.Select(NormalizeLists));
    }

    private static string NormalizeLists(string rule) => rule.Replace("%LISTS%", "");

    private string DesyncTcp(string bin, GuiConfig cfg, int seqovl) =>
        cfg.FakeTlsEnabled
            ? $"--dpi-desync=fake --dpi-desync-ttl=4 --dpi-desync-repeats={cfg.FakeRepeats}"
            : $"--dpi-desync=multisplit --dpi-desync-split-seqovl={seqovl} " +
              $"--dpi-desync-split-pos={cfg.SplitPosition} " +
              $"--dpi-desync-split-seqovl-pattern=\"{bin}tls_clienthello_www_google_com.bin\"";

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
        var text = File.ReadAllText(cfg.CustomProfilePath);
        // из bat/txt профиля извлекаем только строку запуска winws (после winws.exe), убираем переносы ^
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("winws.exe")).ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("В пользовательском профиле не найдена строка запуска winws.exe");
        var joined = string.Join(" ", lines).Replace("^", " ");
        var idx = joined.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) + "winws.exe".Length;
        return joined[idx..].Trim();
    }

    private static string EnsureTrailingSlash(string s) => s.EndsWith('\\') ? s : s + "\\";
}

/// <summary>Генерация аргументов byedpi.exe (bol-van/byedpi).</summary>
public sealed class ByedpiProfileBuilder
{
    public string BuildArgs(GuiConfig cfg)
    {
        var sb = new StringBuilder();
        // byedpi слушает прокси-порты; трафик туда направляется вручную или через прокси-настройки приложения
        sb.Append($"-p{cfg.ByedpiHttpPort} -s{cfg.ByedpiSocksPort} ");
        if (cfg.ByedpiFragAll) sb.Append("-f ..all ");
        if (cfg.ByedpiDisorder) sb.Append("-o ..disorder ");
        if (cfg.ByedpiFind) sb.Append("-e find ");
        if (cfg.ByedpiEofReplace) sb.Append("-Q eof ");
        if (cfg.ByedpiTtlSplit > 0) sb.Append($"-m {cfg.ByedpiTtlSplit} ");
        if (cfg.ByedpiNoFollowRedirects) sb.Append("-n ");
        sb.Append("-w1 -d1"); // защита от WISP-подобных DPI + логирование в stdout
        return sb.ToString().Trim();
    }
}
