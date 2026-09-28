using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hafiza.Services;

namespace Hafiza;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private bool _handlingUiError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            new StorageService().LogError(args.Exception, "خطأ غير متوقع في الواجهة");
            if (_handlingUiError)
            {
                args.Handled = false;
                return;
            }
            _handlingUiError = true;
            args.Handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                _handlingUiError = false;
                if (MainWindow is MainWindow window)
                    window.ShowLoggedErrorNotice();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            new StorageService().LogError(args.Exception, "خطأ في مهمة خلفية");
            args.SetObserved();
        };
        _singleInstance = new Mutex(true, "Hafiza.ArabicClipboard.Singleton", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("حافظة تعمل بالفعل في الخلفية.", "حافظة", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.PrepareForSystemExit();
        base.OnSessionEnding(e);
    }
}
