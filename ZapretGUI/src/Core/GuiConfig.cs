using System.Text.Json.Serialization;

namespace ZapretGUI.Core;

/// <summary>Движок обхода трафика.</summary>
public enum EngineKind
{
    /// <summary>winws.exe из сборки flowseal (zapret/tpws+nfqws на WinDivert).</summary>
    Winws,
    /// <summary>byedpi.exe (bol-van/byedpi) — SOCKS/HTTP-прокси режим.</summary>
    Byedpi,
}

/// <summary>Стратегии обхода — соответствуют bat-файлам flowseal + расширенным опциям zapret.</summary>
public sealed record Strategy(string Id, string Title, string Description);

/// <summary>Постоянные конфигурации пользователя (сериализуются в gui-state.json).</summary>
public sealed class GuiConfig
{
    // ===== Общие =====
    public string Theme { get; set; } = "System";            // Light | Dark | System
    public string AccentHex { get; set; } = "#0067C0";       // цвет акцента Windows 11
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoStartEngineOnLaunch { get; set; }
    public bool ShowNotifications { get; set; } = true;

    // ===== Движок =====
    public EngineKind Engine { get; set; } = EngineKind.Winws;
    public string StrategyId { get; set; } = "general";      // general | alt | alt2 ... | custom
    public bool UseCustomProfile { get; set; }
    public string CustomProfilePath { get; set; } = "";      // путь к .bat/.txt профилю команд

    // ===== winws / zapret параметры (соответствуют key=value из service.bat flowseal) =====
    public bool FilterTCPEnabled { get; set; } = true;
    public string FilterTCPPorts { get; set; } = "80,443,2053,2083,2087,2096,8443";
    public bool FilterUDPEnabled { get; set; } = true;
    public string FilterUDPPorts { get; set; } = "443,19294-19344,50000-50100";

    public bool GameFilterEnabled { get; set; }              // игровые UDP порты (Discord game voice extras)
    public string GameFilterTCP { get; set; } = "";
    public string GameFilterUDP { get; set; } = "";

    public bool DesyncEnabled { get; set; } = true;          // --dpi-desync=multisplit для HTTP(S) по hostlist
    public int SeqovlHttp { get; set; } = 568;               // --dpi-desync-split-seqovl для list-general
    public int SeqovlTls { get; set; } = 681;                // seqovl для discord.media / google
    public int SplitPosition { get; set; } = 1;              // --dpi-desync-split-pos
    public bool FakeTlsEnabled { get; set; }                 // альтернатива: fake TLS ClientHello вместо multisplit
    public bool FakeQuicEnabled { get; set; } = true;        // --dpi-desync=fake для QUIC (UDP 443)
    public int FakeRepeats { get; set; } = 6;                // --dpi-desync-repeats

    public bool DiscordMediaMultisplit { get; set; } = true; // отдельное правило для discord.media (TCP 2053..8443)
    public bool GoogleZeroIpset { get; set; } = true;        // правило list-google + ip-id=zero
    public bool UdpDiscordFake { get; set; } = true;         // --filter-l7=discord,stun dpi-desync=fake
    public bool AnyProtocolUdp { get; set; } = true;         // --dpi-desync-any-protocol для неизвестных UDP

    public bool IpsetEnabled { get; set; } = true;           // использовать ipset-all.txt (IP-база блокируемых сетей)
    public bool AutoUpdateLists { get; set; }                // автообновление списков при старте движка
    public bool CheckUpdatesEnabled { get; set; } = true;    // маркер utils/check_updates.enabled как у flowseal

    // ===== byedpi =====
    [JsonIgnore]
    public int ByedpiSocksPort { get; set; } = 40001;   // SOCKS включается флагом -s без порта (byedpi так работает)
    public bool ByedpiSocksEnabled { get; set; }        // -s : поднимать SOCKS5-прокси
    public int ByedpiHttpPort { get; set; } = 40000;
    public bool ByedpiFragAll { get; set; } = true;          // -f ..all (фрагментация)
    public bool ByedpiDisorder { get; set; } = true;         // -o ..disorder
    public bool ByedpiFind { get; set; }                     // -e find (только помечать)
    public bool ByedpiEofReplace { get; set; }               // -Q eof
    public int ByedpiTtlSplit { get; set; }                  // -m TTL для split, 0 = выкл
    public bool ByedpiNoFollowRedirects { get; set; } = true;// -n

    // ===== Списки =====
    public List<string> UserDomains { get; set; } = new();   // list-general-user.txt
    public List<string> UserExcludes { get; set; } = new();  // list-exclude-user.txt
    public List<string> UserIpset { get; set; } = new();     // ipset-user.txt
    public List<string> UserIpsetExcludes { get; set; } = new();

    // ===== Прочее =====
    public string LastFlowsealVersion { get; set; } = "";
    public string LastZapretVersion { get; set; } = "";
    public DateTime? LastUpdateUtc { get; set; }

    [JsonIgnore]
    public static IReadOnlyList<Strategy> KnownStrategies { get; } = new List<Strategy>
    {
        new("general",  "General (базовая)",              "multisplit+fake по спискам flowseal. Рекомендуется по умолчанию."),
        new("alt",      "ALT (seqovl 650)",               "Вариант с другим смещением фрагментации — помогает при другом DPI."),
        new("alt2",     "ALT2 (fakedsplit)",              "--dpi-desync=fakedsplit для HTTPS-трафика."),
        new("alt3",     "ALT3 (split2 tcp only)",         "Только split2 без seqovl — мягкий вариант для чувствительных каналов."),
        new("alt4",     "ALT4 (fake+multisplit)",         "Комбинация fake + multisplit для ТСПУ с агрессивным DPI."),
        new("alt5",     "ALT5 (autohostlist)",            "Автоматический хост-лист: движок сам учит домены по таймаутам."),
        new("faketls",  "FAKE TLS AUTO",                  "Подмена ClientHello фейком (как general (FAKE TLS AUTO).bat)."),
        new("simplefake","Simple Fake",                   "Простой fake TLS без списков IP — минимальные требования к базе."),
        new("custom",   "Свой профиль (.bat/.txt)",       "Запуск собственного профиля команд из CustomProfilePath."),
    };
}
