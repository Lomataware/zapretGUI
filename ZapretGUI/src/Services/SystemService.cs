using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using ZapretGUI.Core;

namespace ZapretGUI.Services;

/// <summary>
/// Системные интеграции: автозапуск (реестр Run), запуск от администратора,
/// сброс DNS-кэша, открытие настроек Windows, ассоциации. Реализовано без внешних NuGet.
/// </summary>
public sealed class SystemService
{
    private readonly AppPaths _paths;
    private readonly LogService _log;

    public SystemService(AppPaths paths, LogService log) { _paths = paths; _log = log; }

    public static bool IsAdmin =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Автозапуск GUI вместе с Windows (HKCU\...\Run).</summary>
    public void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (enable)
                key.SetValue("ZapretGUI", $"\"{Environment.ProcessPath}\" --minimized");
            else
                key.DeleteValue("ZapretGUI", throwOnMissingValue: false);
            _log.Info($"Автозапуск {(enable ? "включён" : "выключен")}.");
        }
        catch (Exception ex) { _log.Error($"Не удалось изменить автозапуск: {ex.Message}"); }
    }

    public bool GetAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("ZapretGUI") != null;
        }
        catch { return false; }
    }

    /// <summary>Планировщик: задача «ZapretGUI Engine» со стартом при загрузке (для режима службы без GUI).</summary>
    public async Task<(bool ok, string msg)> RegisterStartupTaskAsync(bool enable)
    {
        var exe = Environment.ProcessPath ?? "";
        var args = enable ? "/Create /TN \"ZapretGUI Engine\" /TR \"\\\""+exe+"\\\" --autostart\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F"
                                  : "/Delete /TN \"ZapretGUI Engine\" /F";
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var (exit, outp, err) = await EngineService.RunOnceAsync(psi, TimeSpan.FromSeconds(15));
        var ok = exit == 0 || (!enable && outp.Contains("не существует", StringComparison.OrdinalIgnoreCase));
        return (ok, Truncate(outp + err));
    }

    /// <summary>Сброс кэша DNS и очищение state WinDivert-подобных артефактов после смены стратегии.</summary>
    public async Task FlushDnsCacheAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ipconfig.exe",
            Arguments = "/flushdns",
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        await EngineService.RunOnceAsync(psi, TimeSpan.FromSeconds(10));
        _log.Info("Кэш DNS сброшен (ipconfig /flushdns).");
    }

    /// <summary>Проверить, что запущен именно наш экземпляр winws (по имени процесса).</summary>
    public static bool IsEngineProcessRunning(string processName)
    {
        try { return Process.GetProcessesByName(processName).Length > 0; }
        catch { return false; }
    }

    /// <summary>Убить «осиротевшие» процессы движка (например после падения GUI), как делает service.bat stop.</summary>
    public static async Task KillOrphanEnginesAsync()
    {
        foreach (var name in new[] { "winws", "byedpi" })
            foreach (var p in Process.GetProcessesByName(name))
                try { p.Kill(entireProcessTree: true); } catch { }
        await Task.Delay(300);
    }

    /// <summary>Открыть папку конфигурации в проводнике.</summary>
    public void OpenConfigFolder()
    {
        try
        {
            _paths.EnsureDirs();
            Process.Start(new ProcessStartInfo("explorer.exe", _paths.Root) { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Error($"explorer: {ex.Message}"); }
    }

    /// <summary>Экспорт диагностики: zip логов + конфигов + версий для репортов на GitHub flowseal.</summary>
    public string ExportDiagnostics()
    {
        try
        {
            var dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"zapretgui-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"ZapretGUI diagnostics {DateTime.Now}");
            sb.AppendLine($"OS: {Environment.OSVersion}, 64bit={Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"Admin: {IsAdmin}");
            sb.AppendLine($"Root: {_paths.Root}");
            sb.AppendLine($"winws.exe: {(File.Exists(_paths.WinwsExe) ? new FileInfo(_paths.WinwsExe).LastWriteTime : (DateTime?)null)}");
            sb.AppendLine($"byedpi.exe: {(File.Exists(_paths.ByedpiExe) ? new FileInfo(_paths.ByedpiExe).LastWriteTime : (DateTime?)null)}");
            sb.AppendLine($"list-general.txt lines: {(File.Exists(_paths.ListGeneral) ? File.ReadAllLines(_paths.ListGeneral).Length : -1)}");
            sb.AppendLine($"ipset-all.txt lines: {(File.Exists(_paths.IpsetAll) ? File.ReadAllLines(_paths.IpsetAll).Length : -1)}");
            if (File.Exists(_paths.LogsDir))
                foreach (var f in Directory.GetFiles(_paths.LogsDir, "*.log"))
                {
                    sb.AppendLine($"--- {Path.GetFileName(f)} (tail 200) ---");
                    sb.AppendLine(string.Join("\n", File.ReadAllLines(f).TakeLast(200)));
                }
            File.WriteAllText(dst, sb.ToString());
            return dst;
        }
        catch (Exception ex)
        {
            _log.Error($"Экспорт диагностики: {ex.Message}");
            return "";
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
