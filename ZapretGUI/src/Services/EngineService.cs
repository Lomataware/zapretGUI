using System.Diagnostics;
using System.IO;
using System.Management;
using ZapretGUI.Core;

namespace ZapretGUI.Services;

public enum EngineState { Stopped, Starting, Running, Stopping, Failed }

/// <summary>
/// Управляет жизненным циклом движка: winws.exe (zapret) или byedpi.exe.
/// Поддерживает режимы: фоновый процесс GUI и установка службы Windows (как service.bat у flowseal).
/// </summary>
public sealed class EngineService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly ConfigService _config;
    private readonly LogService _log;
    private Process? _proc;
    private readonly WinwsProfileBuilder _winws;

    public EngineState State { get; private set; } = EngineState.Stopped;
    public event Action<EngineState>? StateChanged;

    public EngineService(AppPaths paths, ConfigService config, LogService log)
    {
        _paths = paths;
        _config = config;
        _log = log;
        _winws = new WinwsProfileBuilder(paths);
    }

    private void SetState(EngineState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public string CurrentExecutable =>
        _config.Config.Engine == EngineKind.Byedpi ? _paths.ByedpiExe : _paths.WinwsExe;

    public string BuildCurrentArgs()
    {
        var cfg = _config.Config;
        return cfg.Engine == EngineKind.Byedpi
            ? new ByedpiProfileBuilder().BuildArgs(cfg)
            : _winws.BuildArgs(cfg);
    }

    /// <summary>Проверка наличия бинарников движка.</summary>
    public (bool ok, string message) ValidateBinaries()
    {
        var cfg = _config.Config;
        if (cfg.Engine == EngineKind.Winws)
        {
            if (!File.Exists(_paths.WinwsExe))
                return (false, "Не найден bin\\winws.exe. Сначала установите сборку flowseal на вкладке «Обновление».");
            if (!File.Exists(Path.Combine(_paths.BinDir, "WinDivert64.sys")))
                return (false, "Не найден bin\\WinDivert64.sys — драйвер перехвата. Переустановите сборку.");
            if (!File.Exists(_paths.ListGeneral))
                return (false, "Не найден lists\\list-general.txt. Нажмите «Обновить конфигурации».");
            return (true, "Готов к запуску");
        }
        else
        {
            if (!File.Exists(_paths.ByedpiExe))
                return (false, "Не найден bin\\byedpi.exe. Скачайте byedpi с github.com/bol-van/byedpi на вкладке «Обновление».");
            return (true, "Готов к запуску");
        }
    }

    /// <summary>Запуск движка как дочернего процесса GUI.</summary>
    public async Task<bool> StartAsync()
    {
        if (State is EngineState.Running or EngineState.Starting) return true;

        var (ok, msg) = ValidateBinaries();
        if (!ok)
        {
            _log.Error(msg);
            SetState(EngineState.Failed);
            return false;
        }

        try
        {
            SetState(EngineState.Starting);
            var args = BuildCurrentArgs();
            _log.Info($"Запуск движка: {Path.GetFileName(CurrentExecutable)} {Summarize(args)}");

            var psi = new ProcessStartInfo
            {
                FileName = CurrentExecutable,
                Arguments = args,
                WorkingDirectory = _paths.Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Write("ENGINE", e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.Write("ENGINE!", e.Data); };
            _proc.Exited += (_, _) =>
            {
                lock (_gate)
                {
                    if (State is EngineState.Running or EngineState.Starting)
                    {
                        var code = SafeExitCode();
                        _log.Warn($"Движок завершился unexpectedly (код {code}).");
                        SetState(code == 0 ? EngineState.Stopped : EngineState.Failed);
                    }
                }
            };

            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            // даём 3 секунды: если процесс жив — успех (WinDivert может просить установку драйвера при первом запуске)
            await Task.Delay(3000);
            if (_proc.HasExited)
            {
                _log.Error($"Движок сразу завершился с кодом {SafeExitCode()}. Проверьте права администратора и WinDivert.");
                SetState(EngineState.Failed);
                return false;
            }

            SetState(EngineState.Running);
            _log.Info("Движок запущен и работает.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Не удалось запустить движок: {ex.Message}");
            SetState(EngineState.Failed);
            return false;
        }
    }

    private readonly object _gate = new();

    public async Task StopAsync()
    {
        if (_proc == null) { SetState(EngineState.Stopped); return; }
        SetState(EngineState.Stopping);
        try
        {
            // штатная остановка zapret по Ctrl+C (GracefulShutdown как в bat-файлах)
            TryConsoleCtrl(_proc);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!_proc.HasExited)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        catch
        {
            try { _proc.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            _proc.Dispose();
            _proc = null;
            SetState(EngineState.Stopped);
            _log.Info("Движок остановлен.");
        }
    }

    private int SafeExitCode() { try { return _proc?.ExitCode ?? -1; } catch { return -1; } }

    /// <summary>Отправка Ctrl+C консольному приложению через AttachConsole + GenerateConsoleCtrlEvent.</summary>
    private static void TryConsoleCtrl(Process p)
    {
        try
        {
            if (NativeMethods.AttachConsole(p.Id))
            {
                NativeMethods.SetConsoleCtrlHandler(null, true);
                NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CTRL_C_EVENT, 0);
                Thread.Sleep(200);
                NativeMethods.FreeConsole();
            }
        }
        catch { /* fallback к Kill внутри StopAsync */ }
    }

    // ===== Служба Windows (winws --service install, аналог service.bat install) =====

    public bool IsServiceInstalled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_Service WHERE Name='zapret' OR Name='byedpi'");
            foreach (var mo in searcher.Get())
                return true;
        }
        catch { }
        return false;
    }

    public async Task<(bool ok, string message)> InstallServiceAsync()
    {
        var exe = CurrentExecutable;
        if (!File.Exists(exe)) return (false, "Бинарник движка не найден.");
        var args = BuildCurrentArgs();
        // winws.exe поддерживает встроенный режим службы (--service install), как использует service.bat flowseal
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--service install {args}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var result = await RunOnceAsync(psi, TimeSpan.FromSeconds(30));
        _log.Info($"Установка службы: {result.output}");
        return result.exit == 0
            ? (true, "Служба «zapret» установлена и запускается автоматически.")
            : (false, $"Не удалось установить службу (код {result.exit}). {Truncate(result.output + result.error, 200)}");
    }

    public async Task<(bool ok, string message)> RemoveServiceAsync()
    {
        var exe = CurrentExecutable;
        if (!File.Exists(exe)) return (false, "Бинарник движка не найден.");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--service uninstall",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var result = await RunOnceAsync(psi, TimeSpan.FromSeconds(30));
        return (result.exit == 0, result.exit == 0 ? "Служба удалена." : Truncate(result.output + result.error, 200));
    }

    public static async Task<(int exit, string output, string error)> RunOnceAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        using var p = new Process { StartInfo = psi };
        var outSb = new System.Text.StringBuilder();
        var errSb = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        return (p.HasExited ? p.ExitCode : -1, outSb.ToString(), errSb.ToString());
    }

    private static string Summarize(string args)
        => args.Length <= 160 ? args : args[..160] + "…";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public void Dispose()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc?.Dispose();
    }
}

internal static class NativeMethods
{
    public const uint CTRL_C_EVENT = 0;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handlerRoutine, bool add);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    public delegate bool ConsoleCtrlDelegate(uint ctrlType);
}
