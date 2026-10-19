using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretGUI.Services;

/// <summary>Простой потокобезопасный логгер в файл + подписка для UI.</summary>
public sealed class LogService
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    public event Action<string>? LineLogged;

    public string LogFilePath { get; private set; } = "";

    public void Open(string logsDir)
    {
        Directory.CreateDirectory(logsDir);
        LogFilePath = Path.Combine(logsDir, $"gui-{DateTime.Now:yyyy-MM-dd}.log");
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = new StreamWriter(LogFilePath, append: true) { AutoFlush = true };
        }
    }

    public void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (_gate) _writer?.WriteLine(line);
        LineLogged?.Invoke(line);
    }

    public void Info(string m) => Write("INFO", m);
    public void Warn(string m) => Write("WARN", m);
    public void Error(string m) => Write("ERROR", m);
}

/// <summary>Загрузка/сохранение GuiConfig в utils/gui-state.json + синхронизация с реестром (автозапуск).</summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public GuiConfig Config { get; private set; } = new();
    private readonly Core.AppPaths _paths;
    private readonly LogService _log;

    public event Action? ConfigSaved;

    public ConfigService(Core.AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_paths.StateFile))
            {
                var json = File.ReadAllText(_paths.StateFile);
                Config = JsonSerializer.Deserialize<GuiConfig>(json, JsonOpts) ?? new GuiConfig();
                _log.Info($"Конфигурация загружена: {_paths.StateFile}");
            }
            else
            {
                Config = new GuiConfig();
                Save();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Не удалось прочитать конфигурацию: {ex.Message}. Используются значения по умолчанию.");
            Config = new GuiConfig();
        }
    }

    public void Save()
    {
        try
        {
            _paths.EnsureDirs();
            File.WriteAllText(_paths.StateFile, JsonSerializer.Serialize(Config, JsonOpts));
            ConfigSaved?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"Ошибка сохранения конфигурации: {ex.Message}");
        }
    }

    /// <summary>Читает текущий конфиг из файла (если внешний процесс его обновил).</summary>
    public void ReloadIfChanged()
    {
        try
        {
            if (File.Exists(_paths.StateFile))
            {
                var json = File.ReadAllText(_paths.StateFile);
                var cfg = JsonSerializer.Deserialize<GuiConfig>(json, JsonOpts);
                if (cfg != null) Config = cfg;
            }
        }
        catch { /* ignore */ }
    }
}
