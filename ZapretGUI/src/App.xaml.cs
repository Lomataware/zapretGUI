using System.Windows;
using ZapretGUI.Core;
using ZapretGUI.Services;
using ZapretGUI.ViewModels;
using ZapretGUI.Views;

namespace ZapretGUI;

public partial class App : Application
{
    public static MainWindowViewModel VM { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Конверторы (используются через StaticResource из MainWindow.xaml)
        Resources["BrushByKey"] = new Controls.BrushByKeyConverter();
        Resources["IntEq"] = new Controls.IntEqualityConverter();
        Resources["IntEqV"] = new Controls.IntToVisibilityConverter();

        var paths = AppPaths.CreateDefault();
        paths.EnsureDirs();
        var log = new LogService();
        log.Open(paths.LogsDir);
        var config = new ConfigService(paths, log);
        config.Load();

        VM = new MainWindowViewModel(paths, log, config);
        ThemeManager.Apply(config.Config.Theme, config.Config.AccentHex);

        var w = new MainWindow { DataContext = VM };
        MainWindow = w;
        w.Show();

        if (config.Config.AutoStartEngineOnLaunch)
            _ = VM.StartEngineAsync(auto: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { VM.Shutdown(); } catch { }
        base.OnExit(e);
    }
}
