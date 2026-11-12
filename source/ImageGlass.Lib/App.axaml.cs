/*
ImageGlass - A Fast, Seamless Photo Viewer
Copyright (C) 2010 - 2026 DUONG DIEU PHAP
Project homepage: https://imageglass.org

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ImageGlass.Common.AppThemes;
using ImageGlass.Common.Localization;
using ImageGlass.Common.Loggers;
using ImageGlass.Common.Photoing;
using ImageGlass.Common.ServiceProviders;
using ImageGlass.Common.ServiceProviders.Licensing;
using ImageGlass.Common.Types;
using ImageGlass.Common.Windows;
using ImageGlass.UI.Windowing;
using ImageGlass.ViewModels;
using ImageGlass.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ImageGlass.Common;

public partial class App : Application
{
    private static MainWindow? _mainWindow = null;
    private static MainWindow? _activeImageWindow;
    private static readonly AsyncLocal<MainWindow?> _commandWindow = new();
    private readonly HashSet<MainWindow> _imageWindows = [];
    private readonly HashSet<MainWindow> _closingImageWindows = [];
    private readonly List<string> _earlyActivatedPaths = [];
    private readonly TaskCompletionSource _startupUiReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isStartupCompleted;
    private bool _isShuttingDown;
    private int _pendingWindowOpens;
    private static Task _updateReconcileTask = Task.CompletedTask;
    private TaskCompletionSource _taskUi = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// How long Linux startup waits for the portal-backed system theme before painting anyway.
    /// Measured at 20-34ms; overshooting it only costs a corrected theme, never the launch.
    /// </summary>
    private const int LINUX_SYSTEM_THEME_GRACE_MS = 100;


    #region Public Properties

    /// <summary>
    /// Gets the main window.
    /// </summary>
    public static MainWindow MainWindow => _commandWindow.Value ?? _mainWindow!;

    /// <summary>The window explicitly associated with the current asynchronous operation.</summary>
    public static MainWindow? CommandWindow => _commandWindow.Value;
    public static MainWindow? PrimaryWindow => _mainWindow;

    /// <summary>Associates an operation and its asynchronous continuations with its originating window.</summary>
    internal static IDisposable UseWindow(MainWindow window) => new WindowScope(window);

    private sealed class WindowScope : IDisposable
    {
        private readonly MainWindow? _previous;
        private bool _disposed;

        public WindowScope(MainWindow window)
        {
            _previous = _commandWindow.Value;
            _commandWindow.Value = window;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _commandWindow.Value = _previous;
        }
    }


    /// <summary>
    /// Gets the settings window.
    /// </summary>
    public static SettingsWindow? SettingsWindow { get; set; }


    /// <summary>
    /// Gets or sets the delegate used to create a new instance of the main application window.
    /// </summary>
    public Func<ImageWindowSession, MainWindow>? CreateMainWindowFn = null;

    #endregion // Public Properties



    #region Instance Initialization

    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    public override void Initialize()
    {
        // App-level exception handler for non-debugger
        if (!Debugger.IsAttached)
        {
            Dispatcher.UIThread.UnhandledException += UIThread_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        AvaloniaXamlLoader.Load(this);
    }


    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    public override async void OnFrameworkInitializationCompleted()
    {
        StartupTrace.Mark("OnFwInit:enter");
        _ = ApplyUIConfigsAsync();
        PlatformSettings?.ColorValuesChanged += PlatformSettings_ColorValuesChanged;

        // localize the macOS application (⌘) menu now and whenever the language changes
        if (OperatingSystem.IsMacOS())
        {
            LocalizeAppMenu();
            Core.LanguageChanged += (_, _) => LocalizeAppMenu();
        }

        // subscribe to activated event to handle app activation from file associations
        var activable = this.TryGetFeature<IActivatableLifetime>();
        activable?.Activated += App_Activated;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // portable mode cannot use the startup dir: report and quit before anything else runs,
            // so the app never falls back to the app data dir
            if (ConfigMode.PortableError is not null)
            {
                MarkStartupAborted();
                await _taskUi.Task;
                await ShowPortableModeErrorAsync();
                return;
            }

            // set shutdown mode
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // get foreground shell; always captured, since a search window is the only source of its own result list
            Core.ShellProvider?.ForegroundShell = Core.ShellProvider.GetForegroundWindowView();

            // set init image path
            Core.UpdateInitImagePath();

            // set main window
            StartupTrace.Mark("MainWindow:ctor:begin");
            CreateMainWindowIfNotExist();
            StartupTrace.Mark("MainWindow:ctor:end");

            // discover native (in-process) codec plugins from the _plugins folder (background)
            Core.DiscoverPlugins();

            // register external (OOP) tools from Config.Tools (igconfig.json)
            Core.RegisterExternalTools();
            StartupTrace.Mark("RegisterTools:done");

            // wait for UI settings ready
            await _taskUi.Task;
            StartupTrace.Mark("Theme:ready");

            desktop.MainWindow = MainWindow;

            // if user settings failed to load, report it first; on Quit the modal exits the app,
            // on Continue we fall through to Quick Setup (which the reset/default config triggers)
            if (Config.LoadingException is not null)
            {
                var isContinue = await ModalWindow.ShowUnhandledErrorAsync(
                    Config.LoadingException, null,
                    "IGE: There was an error while loading user settings");
                if (!isContinue)
                {
                    MarkStartupAborted();
                    return;
                }
            }

            // if an incompatible user config was found and reset, warn before continuing;
            // on No we exit without writing config, on Yes we fall through to Quick Setup
            if (!await ConfirmIncompatibleConfigResetAsync())
            {
                MarkStartupAborted();
                return;
            }

            // force Quick Setup on first run; false = app is exiting/restarting
            // ChuImage starts directly with the configured defaults, without the setup wizard.
            Core.Config.QuickSetupVersion = Const.QUICK_SETUP_VERSION;

            // show main window
            MainWindow.Show();
            StartupTrace.Mark("MainWindow:show");

            _isStartupCompleted = true;
            _startupUiReadyTcs.TrySetResult();
            OpenPendingStartupWindows();

            // an expired license leaves the app in Classic: say so and offer the ways out
            ShowExpiredLicenseNotice();

            // an update that died mid-install would otherwise re-arm itself silently on every launch
            _ = ReportFailedUpdateAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    #endregion // Instance Initialization



    #region Instance Events

    private void App_Activated(object? sender, ActivatedEventArgs e)
    {
        // When the user double-clicks a photo or uses "Open With" on macOS
        if (e is FileActivatedEventArgs fileArgs && fileArgs.Files.Count > 0)
        {
            StartupTrace.Mark("FileActivation:received");
            var modulePath = Core.Args.FirstOrDefault();
            var comparer = PathComparer;
            var resolvedPaths = new List<string>(fileArgs.Files.Count);

            foreach (var file in fileArgs.Files)
            {
                var filePath = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(filePath)) continue;

                var resolved = BHelper.ResolvePath(filePath);
                if (string.IsNullOrEmpty(resolved)) continue;
                if (modulePath is not null && comparer.Equals(resolved, modulePath)) continue;
                if (!resolvedPaths.Contains(resolved, comparer))
                {
                    resolvedPaths.Add(resolved);
                }
            }

            if (resolvedPaths.Count == 0) return;

            // The first file activation can arrive before the window is shown. Feed the first
            // path to the startup window (if empty) and queue additional paths until MainWindow.Show().
            if (!_isStartupCompleted && MainWindow is not null && !MainWindow.IsVisible)
            {
                HandlePreStartupActivatedPaths(resolvedPaths);
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_isShuttingDown) return;

                if (!_isStartupCompleted && MainWindow is not null && !MainWindow.IsVisible)
                {
                    HandlePreStartupActivatedPaths(resolvedPaths);
                    return;
                }

                StartupTrace.Mark("FileActivation:dispatch");
                var source = _activeImageWindow ?? _mainWindow;
                if (source is not null) OpenImagePathsInNewWindows(resolvedPaths, source);
            });
        }

        // When the user reopens the app from the dock on macOS
        else if (e.Kind == ActivationKind.Reopen)
        {
            Dispatcher.UIThread.Post(() =>
            {
                (_activeImageWindow ?? _mainWindow)?.RestoreAndActivate();
            });
        }
    }


    private void AppMenuAbout_Click(object? sender, EventArgs e) => RunAppMenuAction(LangId.Menu_MnuAbout);

    private void AppMenuUpgradeLicense_Click(object? sender, EventArgs e) => RunAppMenuAction(LangId.Menu_MnuUpgradeLicense);

    private void AppMenuManageLicense_Click(object? sender, EventArgs e) => RunAppMenuAction(LangId.Menu_MnuManageLicense);

    private void AppMenuSettings_Click(object? sender, EventArgs e) => RunAppMenuAction(LangId.Menu_MnuSettings);


    /// <summary>
    /// Runs the app action bound to a main-menu language key (shared routing used by the macOS app menu).
    /// </summary>
    private static void RunAppMenuAction(LangId menuKey)
    {
        using var scope = (_activeImageWindow ?? _mainWindow) is { } window ? UseWindow(window) : null;
        var action = AppAPIProvider.GetMenuAction(menuKey);
        if (Core.API is not null) _ = Core.API.RunActionAsync(action);
    }


    /// <summary>
    /// Localizes the macOS application (⌘) menu items declared in App.axaml, matching each item to
    /// its <see cref="LangId"/> via the item's CommandParameter, and assigns the Settings shortcut.
    /// </summary>
    private void LocalizeAppMenu()
    {
        if (NativeMenu.GetMenu(this) is not { } menu) return;

        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            if (item.CommandParameter is not string tag || !Enum.TryParse<LangId>(tag, out var langId)) continue;

            var text = Core.Lang[langId];
            if (!string.IsNullOrWhiteSpace(text)) item.Header = text;

            if (langId == LangId.Menu_MnuSettings)
            {
                item.Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta);
            }

            // show only the licensing item matching the current license state
            if (langId == LangId.Menu_MnuUpgradeLicense) item.IsVisible = !Core.IsProEnabled;
            else if (langId == LangId.Menu_MnuManageLicense) item.IsVisible = Core.IsProEnabled;
        }
    }


    private void PlatformSettings_ColorValuesChanged(object? sender, PlatformColorValues e)
    {
        Core.IsSystemDarkMode = e.ThemeVariant == PlatformThemeVariant.Dark;

        Dispatcher.UIThread.Post(async () =>
        {
            // update color mode for app level
            await ApplyThemePackAsync(Core.IsSystemDarkMode, e.AccentColor1);
        }, DispatcherPriority.Send);
    }


    #region Unhandled Exception Handlers

    private static void UIThread_UnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // must be set synchronously: awaiting first returns with Handled = false, and the
        // dispatcher then rethrows, which kills the UI message loop (app freezes).
        e.Handled = true;
        var ex = e.Exception;

        Dispatcher.UIThread.Post(async () =>
        {
            _ = await ModalWindow.ShowUnhandledErrorAsync(ex);

#if DEBUG
            throw ex;
#endif
        });
    }


    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        var ex = e.Exception;

        Dispatcher.UIThread.Post(async () =>
        {
            await ModalWindow.ShowUnhandledErrorAsync(ex);

#if DEBUG
            throw ex;
#endif
        });
    }


    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = (Exception)e.ExceptionObject;

        // When terminating, the runtime tears the process down as soon as we return, so the
        // posted dialog below would never render: write synchronously or the crash is silent.
        if (e.IsTerminating)
        {
            Console.Error.WriteLine($"Unhandled exception: {BHelper.GetExceptionDetails(ex)}");
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            _ = await ModalWindow.ShowUnhandledErrorAsync(ex);

#if DEBUG
            throw ex;
#endif
        });
    }

    #endregion // Unhandled Exception Handlers

    #endregion // Instance Events



    #region Instance Methods

    /// <summary>
    /// Initializes the application instance, loads configuration,
    /// sets up service providers, and enforces single-instance behavior as configured.
    /// </summary>
    /// <returns><c>true</c> if the application should exit immediately.</returns>
    public static bool InitializeAppInstance(string[] args, Action installServicesFn)
    {
        // 1. use independent culture for formatting or parsing a string
        CultureInfo.DefaultThreadCurrentCulture =
            CultureInfo.DefaultThreadCurrentUICulture =
            Thread.CurrentThread.CurrentCulture =
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;


        // 2. load app configs (merges default, user, CLI -p: args, and admin configs)
        Core.Args = Environment.GetCommandLineArgs();

        // resolve the config dir first: the portable marker moves it to the startup dir
        if (ConfigMode.IsPortable) StartupTrace.Mark("InitInstance:portableMode");

        // verify the Pro license before config (config-independent; admin locks need it)
        Core.AppLicense = LicenseService.LoadActive(out var outOfScopeLicense, out var expiredLicense);
        Core.OutOfScopeLicense = outOfScopeLicense;
        Core.ExpiredLicense = expiredLicense;
        StartupTrace.Mark("InitInstance:licenseLoaded");

        Core.Config = Config.Load(Config.CONFIG_USER, Core.Args);
        StartupTrace.Mark("InitInstance:configLoaded");

        // Initialize lock manager with loaded config
        ServiceProviders.FeatureManager.Refresh();

        // 3. initialize service providers
        installServicesFn();
        StartupTrace.Mark("InitInstance:servicesReady");


        // 4. handle app command lines
        if (App.HandleCommandLineAsync(args).GetAwaiter().GetResult())
        {
            return true;
        }


        // 5. handle single instance; resolve relative paths in the entry process CWD before forwarding,
        // and promote to primary if the running instance has already entered shutdown.
        if (!Core.AppInstance.IsFirstInstance)
        {
            var forwardedArgs = Core.PrepareForwardedArgs(args);
            if (Core.AppInstance.SendArgsOrPromoteToPrimary(AppCmds.SINGLE_INSTANCE, forwardedArgs))
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>
    /// Handles app command-line arguments that should run without starting the UI.
    /// Returns <c>true</c> if the command was handled and the process should exit.
    /// </summary>
    private static async Task<bool> HandleCommandLineAsync(string[] args)
    {
        // enable the opt-in startup profiler if requested (flag may appear anywhere in the args);
        // marks recorded earlier are buffered, so this still captures them on Flush
        StartupTrace.EnableFromArgs(args);

        // enable the opt-in photo-loading profiler if requested (see PhotoTrace)
        PhotoTrace.EnableFromArgs(args);

        if (args.Length < 1) return false;

        var topCmd = args[0];

        // set / remove default photo viewer
        if (topCmd == AppCmds.SET_DEFAULT_PHOTO_VIEWER
            || topCmd == AppCmds.REMOVE_DEFAULT_PHOTO_VIEWER)
        {
            var enable = topCmd == AppCmds.SET_DEFAULT_PHOTO_VIEWER;

            // extensions from arg (";"-joined), or all supported formats when omitted
            var extensions = args.Length >= 2
                ? args[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Core.GetSupportedFileExtensions().ToArray();

            if (Core.ShellProvider is not null)
            {
                await Core.ShellProvider.SetDefaultPhotoViewerAsync(extensions, enable);
            }

            return true;
        }

        return false;
    }


    /// <summary>
    /// Reports the real error behind an unusable portable startup dir, then quits.
    /// </summary>
    private static async Task ShowPortableModeErrorAsync()
    {
        var error = ConfigMode.PortableError!;

        _ = await ModalWindow.ShowErrorAsync(null, new ModalWindowOptions
        {
            Title = BHelper.AppDisplayName,
            Heading = error.Message,
            Details = BHelper.GetExceptionDetails(error),
            ShowInTaskbar = true,
        }, ModalWindowButton.Close);

        BHelper.ExitApp(true);
    }


    /// <summary>
    /// Warns when an incompatible user config was found and reset to defaults.
    /// Returns <c>false</c> after quitting without writing config; <c>true</c> to continue.
    /// </summary>
    private static async Task<bool> ConfirmIncompatibleConfigResetAsync()
    {
        var configPath = Config.IncompatibleUserConfigPath;
        if (string.IsNullOrEmpty(configPath)) return true;

        var result = await ModalWindow.ShowWarningAsync(null, new ModalWindowOptions
        {
            Title = BHelper.AppDisplayName,
            Heading = Core.Lang[LangId._IncompatibleConfig],
            Description = Core.Lang[LangId._IncompatibleConfig_Description],
            Details = configPath,
            Note = Core.Lang[LangId._IncompatibleConfig_BackupNote],
            NoteStyle = InfoBarSeverity.Warning,
            ShowInTaskbar = true,
        }, ModalWindowButton.Yes_No);

        // No / close: quit without writing config (leave the v9 file untouched)
        if (result.ExitCode != DialogExitCode.OK)
        {
            BHelper.ExitApp(true);
            return false;
        }

        return true;
    }


    /// <summary>
    /// Shows the forced startup Quick Setup when required. Returns <c>false</c> if the app is
    /// exiting or restarting, so the caller must not show the main window.
    /// </summary>
    private static async Task<bool> RunStartupQuickSetupAsync()
    {
        // skip when already satisfied, or when relaunched with the suppress flag
        if (!QuickSetupWindow.ShouldShowAtStartup || Core.Args.Contains(AppCmds.NO_QUICK_SETUP))
            return true;

        var wizard = new QuickSetupWindow();
        await wizard.ShowAsync(null);

        // Save already restarted the app
        if (wizard.IsRestarting) return false;

        // Skip: mark done, then restart into a clean process
        if (wizard.DialogResult == DialogExitCode.Cancel)
        {
            Core.Config.QuickSetupVersion = Const.QUICK_SETUP_VERSION;
            if (!await Core.Config.SaveAsync())
            {
                await ModalWindow.ShowSettingsSaveErrorAsync(null, Core.Lang[LangId.QuickSetup_Title]);
            }
            BHelper.RestartApp(suppressQuickSetup: true);
            return false;
        }

        // Close / Alt+F4 / Esc: quit (forced; no main window to close for a graceful shutdown)
        BHelper.ExitApp(true);
        return false;
    }


    /// <summary>
    /// Tells the user their Pro license has expired, once the main window is up.
    /// </summary>
    private static void ShowExpiredLicenseNotice()
    {
        if (Core.ExpiredLicense is null || Core.IsProEnabled) return;

        // posted so startup finishes first; nothing may escape an async void on the UI thread
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                _ = await new ManageLicenseWindow().ShowAsync(MainWindow);
            }
            catch (Exception ex)
            {
                _ = await ModalWindow.ShowUnhandledErrorAsync(ex);
            }
        });
    }


    /// <summary>
    /// Set a new main window.
    /// </summary>
    private void CreateMainWindowIfNotExist()
    {
        if (_mainWindow is not null) return;

        _mainWindow = CreateImageWindow(new ImageWindowSession(Core.Photos, Core.InputImagePathFromArgs, Core.ImageTransform));


        // initialize service providers
        Core.API = new AppAPIProvider();
        _mainWindow.Session.Api = Core.API;
        Core.AppInstance.InstanceInvoked += AppInstance_InstanceInvoked;
        Core.AppInstance.MarkReady();


        // reconcile first and keep the task: the startup UI reports a failed install from it
        _updateReconcileTask = Core.UpdateProvider.ReconcilePendingUpdateAsync();
        _ = InitializeUpdateProviderAsync();

        // an app update can invalidate the launch path baked into an existing registration
        if (Core.ShellProvider is { } shell) _ = Task.Run(shell.RepairDefaultViewerRegistration);
    }

    /// <summary>Creates the same platform window for any image session.</summary>
    internal MainWindow CreateImageWindow(ImageWindowSession session)
    {
        if (_mainWindow is not null) session.Api = new AppAPIProvider(registerTools: false);
        var window = CreateMainWindowFn?.Invoke(session) ?? new MainWindow(session);
        window.DataContext ??= new MainWindowModel(window);
        window.Activated += (_, _) => _activeImageWindow = window;
        _imageWindows.Add(window);
        return window;
    }

    /// <summary>Opens another instance of the shared image window in this process.</summary>
    internal MainWindow OpenImageWindow(string? path, MainWindow? source = null)
    {
        var window = CreateImageWindow(new ImageWindowSession(new PhotoManager(), path));
        if (source is { IsVisible: true })
            window.Position = new PixelPoint(source.Position.X + 24, source.Position.Y + 24);
        try
        {
            window.Show();
        }
        catch
        {
            _imageWindows.Remove(window);
            window.Session.Photos.Dispose();
            throw;
        }
        return window;
    }

    private MainWindow? OpenImagePathsInNewWindows(IReadOnlyList<string> paths, MainWindow? anchor)
    {
        var currentAnchor = anchor ?? _activeImageWindow ?? _mainWindow;
        MainWindow? lastOpened = null;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            currentAnchor = OpenImageWindow(path, currentAnchor);
            lastOpened = currentAnchor;
        }

        return lastOpened;
    }

    private void HandlePreStartupActivatedPaths(IReadOnlyList<string> resolvedPaths)
    {
        if (MainWindow is null) return;

        var startIndex = 0;
        if (string.IsNullOrEmpty(MainWindow.Session.InitialImagePath))
        {
            Core.UpdateInitImagePath(resolvedPaths[0]);
            MainWindow.Session.InitialImagePath = Core.InputImagePathFromArgs;
            StartupTrace.Mark("FileActivation:startup-path");
            startIndex = 1;
        }

        for (var i = startIndex; i < resolvedPaths.Count; i++)
        {
            QueueStartupActivatedPath(resolvedPaths[i]);
        }
    }

    private static StringComparer PathComparer => BHelper.OS == OSType.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private void QueueStartupActivatedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var comparer = PathComparer;
        if (MainWindow?.Session.InitialImagePath is { } initPath && comparer.Equals(initPath, path))
        {
            return;
        }

        if (!_earlyActivatedPaths.Contains(path, comparer))
        {
            _earlyActivatedPaths.Add(path);
        }
    }

    private void OpenPendingStartupWindows()
    {
        if (_isShuttingDown) return;

        var comparer = PathComparer;
        var pendingPaths = new List<string>();
        var extraCliPaths = Core.InputImagePathsFromArgs;
        for (var i = 1; i < extraCliPaths.Count; i++)
        {
            pendingPaths.Add(extraCliPaths[i]);
        }

        foreach (var path in _earlyActivatedPaths)
        {
            if (!pendingPaths.Contains(path, comparer))
            {
                pendingPaths.Add(path);
            }
        }
        _earlyActivatedPaths.Clear();

        if (pendingPaths.Count > 0)
        {
            OpenImagePathsInNewWindows(pendingPaths, MainWindow);
        }
    }

    private void MarkStartupAborted()
    {
        _isShuttingDown = true;
        _earlyActivatedPaths.Clear();
        Core.AppInstance.StopAccepting();
        _startupUiReadyTcs.TrySetResult();
    }

    /// <summary>Runs a shared-setting follow-up in each window's own command context.</summary>
    internal void ForEachImageWindow(Action<MainWindow> action)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ForEachImageWindow(action));
            return;
        }
        foreach (var window in _imageWindows.ToArray())
        {
            if (_closingImageWindows.Contains(window)) continue;
            using var scope = UseWindow(window);
            action(window);
        }
    }

    internal void ReloadAllImageLists()
        => ForEachImageWindow(window =>
        {
            if (window.Session.Photos.DistinctDirs.Count > 0) AppAPIProvider.IG_ReloadList();
        });

    internal void ReloadAllImages()
        => ForEachImageWindow(_ => AppAPIProvider.IG_Reload(false));

    /// <summary>Marks a window as closing and returns whether all image windows are closing.</summary>
    internal bool BeginImageWindowClose(MainWindow window)
    {
        _closingImageWindows.Add(window);
        if (_pendingWindowOpens > 0 || !_imageWindows.All(_closingImageWindows.Contains))
        {
            return false;
        }

        _isShuttingDown = true;
        Core.AppInstance.StopAccepting();
        return true;
    }

    /// <summary>Releases window-owned state and reports whether the process has any image windows left.</summary>
    internal bool ReleaseImageWindow(MainWindow window)
    {
        _closingImageWindows.Remove(window);
        if (!_imageWindows.Remove(window)) return false;
        window.Session.ClipboardImage?.Dispose();
        window.Session.ClipboardImage = null;
        window.Session.DestColorProfile?.Dispose();
        window.Session.DestColorProfile = null;
        if (ReferenceEquals(_activeImageWindow, window)) _activeImageWindow = _imageWindows.FirstOrDefault();
        window.Session.Photos.Dispose();
        if (ReferenceEquals(_mainWindow, window))
        {
            _mainWindow = _imageWindows.FirstOrDefault();
            if (_mainWindow is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = _mainWindow;
        }
        if (_imageWindows.Count != 0 || _pendingWindowOpens > 0) return false;
        _isShuttingDown = true;
        Core.AppInstance.StopAccepting();
        Core.AppInstance.InstanceInvoked -= AppInstance_InstanceInvoked;
        return true;
    }

    private void ShutdownIfNoWindowsRemain()
    {
        if (_imageWindows.Count != 0 || _pendingWindowOpens > 0) return;

        _isShuttingDown = true;
        Core.AppInstance.StopAccepting();
        Core.AppInstance.InstanceInvoked -= AppInstance_InstanceInvoked;
        Core.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void AppInstance_InstanceInvoked(AppInstance sender, InstanceInvokedEventArgs e)
    {
        if (e.Command != AppCmds.SINGLE_INSTANCE || _isShuttingDown || !Core.AppInstance.IsAccepting) return;
        if (!_isStartupCompleted && _mainWindow is null) return;
        if (_imageWindows.Count > 0 && _imageWindows.All(_closingImageWindows.Contains)) return;

        e.Accepted = true;
        _pendingWindowOpens++;
        _ = HandleInstanceInvokedAsync(e.Arguments);
    }

    private async Task HandleInstanceInvokedAsync(string[] forwardedArgs)
    {
        try
        {
            await _taskUi.Task;
            await _startupUiReadyTcs.Task;
            if (_isShuttingDown) return;

            var window = _activeImageWindow ?? _mainWindow;
            if (window is null) return;

            using var scope = UseWindow(window);
            var modulePath = Core.Args.ElementAtOrDefault(0) ?? string.Empty;
            Core.Args = [modulePath, .. forwardedArgs];
            Core.UpdateInitImagePath();

            var changedIds = Config.ApplyCliOverrides(Core.Config, forwardedArgs);
            ServiceProviders.FeatureManager.Refresh();
            if (changedIds.Count > 0)
            {
                SettingsViewModel.RunApplyActions(changedIds);
            }

            var paths = Core.InputImagePathsFromArgs;
            if (paths.Count == 0)
            {
                if (!_closingImageWindows.Contains(window))
                {
                    window.RestoreAndActivate();
                }
                else if (_imageWindows.FirstOrDefault(w => !_closingImageWindows.Contains(w)) is { } fallback)
                {
                    fallback.RestoreAndActivate();
                }
                return;
            }

            OpenImagePathsInNewWindows(paths, window);
        }
        finally
        {
            _pendingWindowOpens = Math.Max(0, _pendingWindowOpens - 1);
            ShutdownIfNoWindowsRemain();
        }
    }


    /// <summary>
    /// Fires the silent update check once the pending-update flag has been reconciled.
    /// </summary>
    private static async Task InitializeUpdateProviderAsync()
    {
        await _updateReconcileTask;

        // silent check handles disabled/interval logic
        _ = await Core.API.RunApiAsync(API.IG_CheckForUpdate, "false");
    }


    /// <summary>
    /// Reports an install that Windows rejected after shutting the previous session down.
    /// </summary>
    private static async Task ReportFailedUpdateAsync()
    {
        await _updateReconcileTask;

        if (Core.UpdateProvider.LastApplyFailure is { } failure)
        {
            await AppAPIProvider.ShowUpdateFailedAsync(failure);
        }
    }


    /// <summary>
    /// Applies user interface settings, including base styles, theme, and language preferences.
    /// </summary>
    private async Task ApplyUIConfigsAsync()
    {
        try
        {
            // update the base styles
            Core.UpdateBaseResources();


            // Linux resolves the system theme asynchronously, so the first read is a provisional
            // Light; let ColorValuesChanged correct it first, or a dark desktop flashes light
            var themeApplied = false;
            if (BHelper.OS == OSType.Linux)
            {
                await Task.WhenAny(_taskUi.Task, Task.Delay(LINUX_SYSTEM_THEME_GRACE_MS));

                // completing the task is the last thing ApplyThemePackAsync does
                themeApplied = _taskUi.Task.IsCompleted;
            }


            // load theme for the first time
            if (!themeApplied)
            {
                var info = PlatformSettings!.GetColorValues();
                var isSystemDarkMode = info.ThemeVariant == PlatformThemeVariant.Dark;

                // sync the global: ColorValuesChanged only fires on later OS theme changes, so without
                // this Core.IsSystemDarkMode would stay at its default and mis-resolve live theme re-applies
                Core.IsSystemDarkMode = isSystemDarkMode;

                try
                {
                    await ApplyThemePackAsync(isSystemDarkMode, info.AccentColor1);
                }
                catch (Exception ex)
                {
                    var isContinue = await ModalWindow.ShowUnhandledErrorAsync(ex);
                    if (!isContinue) return;
                }
            }


            // initialize Magick decoder on background thread
            _ = Task.Run(MagickCodec.Initialize);

            // load app language
            _ = Core.Config.LoadCurrentLanguageAsync();
        }
        finally
        {
            // the main window is gated on this task: leaving it uncompleted starts the app with no
            // visible window and no error at all, so it must be set on every path out of here
            _ = _taskUi.TrySetResult();
        }
    }


    /// <summary>
    /// Applies the current theme pack and accent color to the app, updating UI resources as needed.
    /// </summary>
    /// <param name="systemAccentColor">
    /// The raw OS accent color. Pass <c>null</c> (e.g. on a live re-apply) to read it fresh from the
    /// platform, so a pack that follows the system accent always resolves the true OS accent.
    /// </param>
    public async Task ApplyThemePackAsync(bool isSystemDarkMode, Color? systemAccentColor = null)
    {
        // load theme pack
        var darkMode = Core.Config.AppearanceMode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => isSystemDarkMode,
        };
        var hasThemeChanged = await Core.Config.LoadCurrentThemeAsync(darkMode,
                useFallBackTheme: true,
                throwIfThemeInvalid: true,
                forceUpdateBackground: false);

        // load & compute accent colors
        var systemAccent = systemAccentColor
            ?? PlatformSettings?.GetColorValues().AccentColor1
            ?? Core.AccentColor;
        var accent = Core.Theme.UseSystemAccent
            ? systemAccent
            : Core.Theme.AccentColor;
        var hasAccentChanged = Core.SetAccentColor(accent);


        // set UI according to theme pack
        Core.SetAppDarkThemeVariant(Core.Theme.Settings.IsDarkMode);

        if (hasAccentChanged || hasThemeChanged)
        {
            Core.UpdateAccentColorResources();
            AppThemeColors.Load(Core.Theme.Colors, accent);
            Core.UpdateAppThemedColorResources();
        }

        if (hasThemeChanged)
        {
            Core.OnThemeChanged();
        }

        StartupTrace.Mark("ApplyThemePack:done");
        _ = _taskUi.TrySetResult();
    }


    #endregion // Instance Methods



}
