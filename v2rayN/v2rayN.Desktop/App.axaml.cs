using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop;

public partial class App : Application
{
    private static CancellationTokenSource? _macOSRestoreCts;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var viewLocator = SimpleViewLocator.Instance;
        DataTemplates.Add(viewLocator);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!Design.IsDesignMode)
            {
                AppManager.Instance.InitComponents();
                DataContext = StatusBarViewModel.Instance;
            }

            var mainWindowViewModel = new MainWindowViewModel();
            var mainWindow = (MainWindow)viewLocator.Build(mainWindowViewModel);
            mainWindow.ViewModel = mainWindowViewModel;
            desktop.MainWindow = mainWindow;

            if (OperatingSystem.IsMacOS())
            {
                Current?.TryGetFeature<IActivatableLifetime>()?.Activated += OnMacOSActivated;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
  
    #region MacOS Activation

    private void OnMacOSActivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind != ActivationKind.Reopen)
        {
            return;
        }

        if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not MainWindow mainWindow)
        {
            return;
        }
  
        var isMiniaturized = MacAppUtils.IsWindowMiniaturized(mainWindow);

        Dispatcher.UIThread.Post(() =>
        {
            // Cancel any in-flight restore chain from a previous Reopen before starting a new one,
            // so repeated Dock clicks don't queue up multiple overlapping Activate()/Focus() calls.
            _macOSRestoreCts?.Cancel();
            _macOSRestoreCts?.Dispose();
            _macOSRestoreCts = null;
  
            if (isMiniaturized)
            {
                var cts = new CancellationTokenSource();
                _macOSRestoreCts = cts;
                RestoreMacOSAccessoryPolicyAfterMiniaturize(mainWindow, cts.Token);
                mainWindow.ShowHideWindow(true);
                return;
            }
  
            if (!AppManager.Instance.Config.UiItem.MacOSShowInDock)
            {
                MacAppUtils.SetActivationPolicyAccessory();
            }

            mainWindow.ShowHideWindow(true);
        });
    }

    private static void RestoreMacOSAccessoryPolicyAfterMiniaturize(MainWindow mainWindow, CancellationToken token)
    {
        if (AppManager.Instance.Config.UiItem.MacOSShowInDock)
        {
            return;
        }

        mainWindow
            .GetObservable(Window.WindowStateProperty)
            .Skip(1)
            .Where(state => state != WindowState.Minimized)
            .Take(1)
            .TakeWhile(_ => !token.IsCancellationRequested)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => QueueMacOSAccessoryPolicyRestore(mainWindow, token));
    }
  
    private static void QueueMacOSAccessoryPolicyRestore(MainWindow mainWindow, CancellationToken token, int attempt = 0)
    {
        const int maxAttempts = 10; // ~10 * 300ms = 3s upper bound before giving up
        if (token.IsCancellationRequested)
        {
            return;
        }

        // AppKit may keep isMiniaturized set until the Dock restore animation finishes.
        DispatcherTimer.RunOnce(() =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (AppManager.Instance.Config.UiItem.MacOSShowInDock)
            {
                return;
            }

            if (MacAppUtils.IsWindowMiniaturized(mainWindow))
            {
                // Native state hasn't caught up yet; retry instead of silently giving up,
                // but bail out after maxAttempts so we never retry forever.
                if (attempt + 1 < maxAttempts)
                {
                    QueueMacOSAccessoryPolicyRestore(mainWindow, token, attempt + 1);
                }
                return;
            }

            RestoreMacOSAccessoryPolicy(mainWindow);
        }, TimeSpan.FromMilliseconds(300));
    }
  
    private static void RestoreMacOSAccessoryPolicy(MainWindow mainWindow)
    {
        if (AppManager.Instance.Config.UiItem.MacOSShowInDock || MacAppUtils.IsWindowMiniaturized(mainWindow))
        {
            return;
        }
  
        MacAppUtils.SetActivationPolicyAccessory();

        // Only (re)activate here if the window isn't already the active/focused window,
        // to avoid firing a second Activate()/Focus() right after ShowHideWindow() already did.
        if (!mainWindow.IsActive)
        {
            mainWindow.Activate();
            mainWindow.Focus();
        }
    }

    #endregion MacOS Activation

    #region App Event

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject != null)
        {
            Logging.SaveLog("CurrentDomain_UnhandledException", (Exception)e.ExceptionObject);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logging.SaveLog("TaskScheduler_UnobservedTaskException", e.Exception);
    }

    private async void MenuAddServerViaClipboardClick(object? sender, EventArgs e)
    {
        try
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: not null })
            {
                AppEvents.AddServerViaClipboardRequested.Publish();
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MenuAddServerViaClipboardClick", ex);
        }
    }

    private async void MenuExit_Click(object? sender, EventArgs e)
    {
        await AppManager.Instance.AppExitAsync(false);
        AppManager.Instance.Shutdown(true);
    }

    #endregion App Event
}
