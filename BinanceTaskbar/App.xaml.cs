using Application = System.Windows.Application;
using System.Threading;
using System.Windows;
using BinanceTaskbar.Services;

namespace BinanceTaskbar;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\BinanceTaskbar.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var settings = SettingsService.Load();
        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}



