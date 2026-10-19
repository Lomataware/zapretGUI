using System.Collections.ObjectModel;
using ZapretGUI.Core;
using ZapretGUI.Services;
// Алиасы, чтобы поле `System` (SystemService) не затеняло пространство имён System.*
using SysDiag = System.Diagnostics;
using SysIO = System.IO;
using SysWin = System.Windows;

namespace ZapretGUI.ViewModels;

/// <summary>Элемент навигации/стратегии с подписями на русском.</summary>
public sealed record ComboItem(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Строка результата теста блокировок.</summary>
public sealed class TestRowViewModel : ObservableObject
{
    private CheckVerdict _verdict = CheckVerdict.Unknown;
    private string _detail = "—";
    private string _latency = "";
    private bool _running;

    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public string Target { get; init; } = "";

    public CheckVerdict Verdict
    {
        get => _verdict;
        set
        {
            if (Set(ref _verdict, value))
            {
                OnPropertyChanged(nameof(VerdictText));
                OnPropertyChanged(nameof(VerdictColorKey));
            }
        }
    }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Latency { get => _latency; set => Set(ref _latency, value); }
    public bool Running { get => _running; set => Set(ref _running, value); }

    public string VerdictText => Verdict switch
    {
        CheckVerdict.Running => "Работает",
        CheckVerdict.Blocked => "Заблокировано",
        CheckVerdict.PartiallyBroken => "Частично",
        CheckVerdict.NotTested => "Не проверено",
        _ => "Ожидание",
    };

    /// <summary>Ключ кисти из словаря темы (Badge*/Success/Error).</summary>
    public string VerdictColorKey => Verdict switch
    {
        CheckVerdict.Running => "SuccessBrush",
        CheckVerdict.Blocked => "ErrorBrush",
        CheckVerdict.PartiallyBroken => "WarningBrush",
        _ => "TextSecondaryBrush",
    };
}

/// <summary>Главная ViewModel приложения.</summary>
public sealed class MainWindowViewModel : ObservableObject
{
    public AppPaths Paths { get; }
    public LogService Log { get; }
    public ConfigService ConfigService { get; }
    public GuiConfig Cfg => ConfigService.Config;

    public EngineService Engine { get; }
    public UpdateService Updater { get; }
    public BlockTestService Tester { get; }
    public ListService Lists { get; }
    public SystemService System { get; }

    // ===== Навигация =====
    private int _selectedPage;
    public int SelectedPage { get => _selectedPage; set { if (Set(ref _selectedPage, value)) OnPropertyChanged(nameof(CurrentPageTitle)); } }
    public string CurrentPageTitle => SelectedPage switch
    {
        0 => "Главная",
        1 => "Настройки движка",
        2 => "Списки и профили",
        3 => "Тест блокировок",
        4 => "Обновление",
        _ => "Приложение",
    };

    // ===== Состояние движка =====
    private string _engineStateText = "Остановлен";
    public string EngineStateText { get => _engineStateText; set => Set(ref _engineStateText, value); }

    private string _engineStateColorKey = "TextSecondaryBrush";
    public string EngineStateColorKey { get => _engineStateColorKey; set => Set(ref _engineStateColorKey, value); }

    private string _currentCommandPreview = "";
    public string CurrentCommandPreview { get => _currentCommandPreview; set => Set(ref _currentCommandPreview, value); }

    // ===== Логи =====
    public ObservableCollection<string> LogLines { get; } = new();
    private string _logBox = "";
    public string LogBox { get => _logBox; set => Set(ref _logBox, value); }

    // ===== Тесты =====
    public ObservableCollection<TestRowViewModel> TestRows { get; } = new();
    private double _testProgress;
    public double TestProgress { get => _testProgress; set => Set(ref _testProgress, value); }
    private bool _testsRunning;
    public bool TestsRunning { get => _testsRunning; set { if (Set(ref _testsRunning, value)) { OnPropertyChanged(nameof(CanStartEngine)); OnPropertyChanged(nameof(CanStopEngine)); } } }

    // ===== Обновление =====
    private double _updateProgress;
    public double UpdateProgress { get => _updateProgress; set => Set(ref _updateProgress, value); }
    private string _updateStatus = "Готово к проверке обновлений.";
    public string UpdateStatus { get => _updateStatus; set => Set(ref _updateStatus, value); }
    private bool _updateRunning;
    public bool UpdateRunning { get => _updateRunning; set => Set(ref _updateRunning, value); }
    private string _flowsealVersionInfo = "не установлено";
    public string FlowsealVersionInfo { get => _flowsealVersionInfo; set => Set(ref _flowsealVersionInfo, value); }
    private string _byedpiVersionInfo = "не установлено";
    public string ByedpiVersionInfo { get => _byedpiVersionInfo; set => Set(ref _byedpiVersionInfo, value); }

    // ===== Списки =====
    private string _domainsText = "";
    public string DomainsText { get => _domainsText; set => Set(ref _domainsText, value); }
    private string _excludesText = "";
    public string ExcludesText { get => _excludesText; set => Set(ref _excludesText, value); }
    private string _ipsetText = "";
    public string IpsetText { get => _ipsetText; set => Set(ref _ipsetText, value); }
    private string _listCountsInfo = "";
    public string ListCountsInfo { get => _listCountsInfo; set => Set(ref _listCountsInfo, value); }

    // ===== Прочее =====
    private bool _serviceInstalled;
    public bool ServiceInstalled { get => _serviceInstalled; set => Set(ref _serviceInstalled, value); }
    private string _adminInfo = "";
    public string AdminInfo { get => _adminInfo; set => Set(ref _adminInfo, value); }

    public IReadOnlyList<ComboItem> ThemeItems { get; } = new List<ComboItem>
    {
        new("System", "Как в системе"), new("Light", "Светлая"), new("Dark", "Тёмная"),
    };
    public IReadOnlyList<ComboItem> StrategyItems { get; } =
        GuiConfig.KnownStrategies.Select(s => new ComboItem(s.Id, s.Title)).ToList();
    public IReadOnlyList<ComboItem> EngineItems { get; } = new List<ComboItem>
    {
        new("Winws", "Zapret (winws / flowseal)"), new("Byedpi", "byedpi (SOCKS/HTTP прокси)"),
    };

    public RelayCommand StartEngineCommand { get; }
    public RelayCommand StopEngineCommand { get; }
    public RelayCommand RestartEngineCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand ApplyThemeCommand { get; }
    public RelayCommand RunTestsCommand { get; }
    public RelayCommand CancelTestsCommand { get; }
    public RelayCommand UpdateAllCommand { get; }
    public RelayCommand UpdateFlowsealCommand { get; }
    public RelayCommand UpdateBasesCommand { get; }
    public RelayCommand UpdateByedpiCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand FlushDnsCommand { get; }
    public RelayCommand InstallServiceCommand { get; }
    public RelayCommand RemoveServiceCommand { get; }
    public RelayCommand SaveListsCommand { get; }
    public RelayCommand ReloadListsCommand { get; }
    public RelayCommand CopyCommandLineCommand { get; }
    public RelayCommand ExportLogsCommand { get; }
    public RelayCommand OpenGithubFlowsealCommand { get; }
    public RelayCommand OpenGithubZapretCommand { get; }

    private CancellationTokenSource? _testsCts;

    public MainWindowViewModel(AppPaths paths, LogService log, ConfigService config)
    {
        Paths = paths; Log = log; ConfigService = config;
        Lists = new ListService(paths, config, log);
        Updater = new UpdateService(paths, log);
        Engine = new EngineService(paths, config, log);
        Tester = new BlockTestService(log);
        System = new SystemService(paths, log);

        // подписки
        Engine.StateChanged += s => OnUi(() =>
        {
            (EngineStateText, EngineStateColorKey) = s switch
            {
                EngineState.Running => ("Движок работает", "SuccessBrush"),
                EngineState.Starting => ("Запуск…", "WarningBrush"),
                EngineState.Stopping => ("Остановка…", "WarningBrush"),
                EngineState.Failed => ("Ошибка запуска", "ErrorBrush"),
                _ => ("Остановлен", "TextSecondaryBrush"),
            };
            RefreshCommandPreview();
        });
        Log.LineLogged += line => OnUi(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 800) LogLines.RemoveAt(0);
            LogBox = string.Join("\n", LogLines);
        });
        Updater.ProgressChanged += m => OnUi(() => UpdateStatus = m);
        Updater.ProgressPercent += p => OnUi(() => UpdateProgress = p * 100);
        Tester.ResultUpdated += r => OnUi(() =>
        {
            var row = TestRows.FirstOrDefault(t => t.Name == r.Name);
            if (row == null) return;
            row.Verdict = r.Verdict; row.Detail = r.Detail;
            row.Latency = r.Latency.TotalMilliseconds > 0 ? $"{r.Latency.TotalMilliseconds:0} мс" : "";
            row.Running = false;
        });

        StartEngineCommand = new RelayCommand(async () => await StartEngineAsync(false), () => !TestsRunning && CanStartEngine);
        StopEngineCommand = new RelayCommand(async () => await Engine.StopAsync(), () => CanStopEngine);
        RestartEngineCommand = new RelayCommand(async () => { await Engine.StopAsync(); await Task.Delay(500); await StartEngineAsync(false); }, () => CanStopEngine);
        SaveSettingsCommand = new RelayCommand(() => { SaveConfig(); RefreshCommandPreview(); Log.Info("Настройки сохранены."); });
        ApplyThemeCommand = new RelayCommand(() => { ThemeManager.Apply(Cfg.Theme, Cfg.AccentHex); SaveConfig(); });
        RunTestsCommand = new RelayCommand(async () => await RunTestsAsync(), () => !TestsRunning && !UpdateRunning);
        CancelTestsCommand = new RelayCommand(() => _testsCts?.Cancel(), () => TestsRunning);
        UpdateAllCommand = new RelayCommand(async () => await UpdateAllAsync(), () => !UpdateRunning);
        UpdateFlowsealCommand = new RelayCommand(async () => await GuardedUpdate(() => Updater.UpdateFlowsealAsync(Cfg, CancellationToken.None)), () => !UpdateRunning);
        UpdateBasesCommand = new RelayCommand(async () => await GuardedUpdate(async () => { var n = await Updater.UpdateDomainBasesAsync(Cfg, CancellationToken.None); return $"Добавлено доменов: {n}"; }), () => !UpdateRunning);
        UpdateByedpiCommand = new RelayCommand(async () => await GuardedUpdate(() => Updater.UpdateByedpiAsync(CancellationToken.None)), () => !UpdateRunning);
        OpenFolderCommand = new RelayCommand(() => System.OpenConfigFolder());
        FlushDnsCommand = new RelayCommand(async () => { await System.FlushDnsCacheAsync(); Log.Info("Кэш DNS очищен (ipconfig /flushdns)."); });
        InstallServiceCommand = new RelayCommand(async () => { var (ok, msg) = await Engine.InstallServiceAsync(); Log.Info(msg); ServiceInstalled = ok || Engine.IsServiceInstalled(); });
        RemoveServiceCommand = new RelayCommand(async () => { var (_, msg) = await Engine.RemoveServiceAsync(); Log.Info(msg); ServiceInstalled = Engine.IsServiceInstalled(); });
        SaveListsCommand = new RelayCommand(SaveLists);
        ReloadListsCommand = new RelayCommand(LoadLists);
        CopyCommandLineCommand = new RelayCommand(() =>
        {
            try { SysWin.Clipboard.SetText(Engine.BuildCurrentArgs()); Log.Info("Командная строка движка скопирована в буфер."); } catch { }
        });
        ExportLogsCommand = new RelayCommand(() =>
        {
            try { SysDiag.Process.Start("explorer.exe", $"\"{Paths.LogsDir}\""); } catch { }
        });
        OpenGithubFlowsealCommand = new RelayCommand(() => OpenUrl(AppUrls.FlowsealReleasesPage));
        OpenGithubZapretCommand = new RelayCommand(() => OpenUrl(AppUrls.ZapretDocsFaq));

        // начальные данные
        LoadLists();
        RefreshCommandPreview();
        AdminInfo = SystemService.IsAdmin
            ? "Программа запущена от администратора — всё готово к работе."
            : "Требуются права администратора! Перезапустите ZapretGUI elevated (manifest должен был запросить UAC).";
        ServiceInstalled = Engine.IsServiceInstalled();
        FlowsealVersionInfo = string.IsNullOrEmpty(Cfg.LastFlowsealVersion) ? "не установлено" : Cfg.LastFlowsealVersion;
        ByedpiVersionInfo = string.IsNullOrEmpty(Cfg.LastZapretVersion) ? "не установлено" : Cfg.LastZapretVersion;
        UpdateTestRowsFromTargets();
    }

    public bool CanStartEngine => Engine.State is EngineState.Stopped or EngineState.Failed;
    public bool CanStopEngine => Engine.State is EngineState.Running or EngineState.Starting;

    private static void OnUi(Action a)
    {
        var d = SysWin.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.Invoke(a);
    }

    public async Task StartEngineAsync(bool auto)
    {
        SaveConfig();
        var ok = await Engine.StartAsync();
        if (!ok && auto) Log.Warn("Автозапуск движка не удался — проверьте настройки на вкладке «Главная».");
    }

    // ==================== ТЕСТЫ ====================
    private void UpdateTestRowsFromTargets()
    {
        TestRows.Clear();
        foreach (var t in Tester.AllTargets())
            TestRows.Add(new TestRowViewModel { Category = t.Category, Name = t.Name, Target = t.Target });
    }

    public async Task RunTestsAsync()
    {
        _testsCts = new CancellationTokenSource();
        TestsRunning = true;
        foreach (var r in TestRows) { r.Verdict = CheckVerdict.Unknown; r.Detail = "…"; r.Latency = ""; r.Running = true; }
        var progress = new Progress<double>(p => OnUi(() => TestProgress = p * 100));
        try
        {
            Log.Info("Запуск тестов работоспособности в условиях блокировок РФ…");
            await Tester.RunAllAsync(progress, _testsCts.Token);
            Log.Info("Тесты завершены.");
        }
        catch (OperationCanceledException) { Log.Warn("Тесты отменены пользователем."); }
        finally
        {
            foreach (var r in TestRows.Where(r => r.Running)) { r.Running = false; if (r.Verdict == CheckVerdict.Unknown) r.Verdict = CheckVerdict.NotTested; }
            TestsRunning = false;
            TestProgress = 0;
        }
    }

    // ==================== ОБНОВЛЕНИЕ ====================
    private async Task UpdateAllAsync()
    {
        try
        {
            UpdateRunning = true; UpdateProgress = 0;
            await Updater.UpdateFlowsealAsync(Cfg, CancellationToken.None);
            await Updater.UpdateDomainBasesAsync(Cfg, CancellationToken.None);
            await Updater.UpdateByedpiAsync(CancellationToken.None);
            Cfg.LastUpdateUtc = DateTime.UtcNow;
            SaveConfig();
            OnUi(() =>
            {
                FlowsealVersionInfo = Cfg.LastFlowsealVersion;
                UpdateStatus = "Все компоненты обновлены. Готово.";
            });
            Log.Info("Полное обновление завершено.");
            LoadLists();
        }
        catch (Exception ex) { Log.Error($"Ошибка обновления: {ex.Message}"); OnUi(() => UpdateStatus = $"Ошибка: {ex.Message}"); }
        finally { UpdateRunning = false; }
    }

    private async Task GuardedUpdate(Func<Task<string>> action)
    {
        try
        {
            UpdateRunning = true; UpdateProgress = 0;
            var res = await action();
            SaveConfig();
            OnUi(() => { UpdateStatus = res; FlowsealVersionInfo = string.IsNullOrEmpty(Cfg.LastFlowsealVersion) ? "не установлено" : Cfg.LastFlowsealVersion; });
        }
        catch (Exception ex) { Log.Error($"Ошибка обновления: {ex.Message}"); OnUi(() => UpdateStatus = $"Ошибка: {ex.Message}"); }
        finally { UpdateRunning = false; }
    }

    // ==================== СПИСКИ ====================
    public void LoadLists()
    {
        DomainsText = string.Join("\n", Lists.LoadDomains());
        ExcludesText = string.Join("\n", Lists.LoadExcludes());
        IpsetText = string.Join("\n", Lists.LoadIpset());
        var c = Lists.GetCounts();
        ListCountsInfo = $"list-general: {c.general}, list-google: {c.google}, ipset-all: {c.ipset}, user-домены: {c.userD}, excludes: {c.userE}";
    }

    private void SaveLists()
    {
        Lists.SaveDomains(DomainsText.Split('\n'));
        Lists.SaveExcludes(ExcludesText.Split('\n'));
        Lists.SaveIpset(IpsetText.Split('\n'));
        LoadLists();
        RefreshCommandPreview();
        Log.Info("Пользовательские списки сохранены.");
    }

    // ==================== ПРОЧЕЕ ====================
    public void SaveConfig() => ConfigService.Save();

    public void RefreshCommandPreview()
    {
        try
        {
            var exe = SysIO.Path.GetFileName(Engine.CurrentExecutable);
            CurrentCommandPreview = exe + " " + Engine.BuildCurrentArgs();
        }
        catch (Exception ex) { CurrentCommandPreview = "Ошибка формирования командной строки: " + ex.Message; }
    }

    private void OpenUrl(string url)
    {
        try { SysDiag.Process.Start(new SysDiag.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Не удалось открыть ссылку: {ex.Message}"); }
    }

    public void Shutdown()
    {
        _testsCts?.Cancel();
        SaveConfig();
        Engine.Dispose();
    }
}
