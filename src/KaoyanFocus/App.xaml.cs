using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;

namespace KaoyanFocus;

public partial class App : Application
{
    readonly StateStore store = new(StateStore.DefaultPath);
    AppState state = null!;
    Mutex? instanceMutex;
    bool ownsInstanceMutex;
    LockWindow? lockWindow;
    readonly List<ShieldWindow> shieldWindows = [];
    StrictModeController? strictModeController;
    FocusWatchdogSession? watchdog;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (WatchdogCommand.TryParse(e.Args, out var watchdogCommand))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            await FocusWatchdogHost.RunAsync(watchdogCommand);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var acquisition = SingleInstanceOwnership.Acquire(() =>
        {
            var handle = new Mutex(true, "KaoyanFocus.SingleInstance", out var ownsMutex);
            return (handle, ownsMutex);
        });
        if (!acquisition.Succeeded)
        {
            MessageBox.Show(acquisition.ErrorMessage, "考研自律神器", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        instanceMutex = acquisition.Handle;
        ownsInstanceMutex = acquisition.OwnsMutex;
        if (!ownsInstanceMutex)
        {
            MessageBox.Show("考研自律神器已经在运行。");
            Shutdown();
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        state = store.LoadOrCreate(today);
        if (state.Started && !state.EmergencyMode && !FocusRules.AllTasksComplete(state))
        {
            ShowLock();
            return;
        }

        ShowMain();
        if (FocusRules.ShouldRefreshExamDate(state.ExamDate, state.ExamDateCheckedAt, today, DateTimeOffset.Now))
            await RefreshExamDate(today);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        strictModeController?.Dispose();
        strictModeController = null;
        SingleInstanceOwnership.ReleaseIfOwned(
            ownsInstanceMutex,
            () => instanceMutex?.ReleaseMutex());
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        EndFocusSession();
        base.OnSessionEnding(e);
    }

    void ShowMain()
    {
        var window = new MainWindow(state, store);
        window.ExamDateRefreshRequested += today => _ = RefreshExamDate(today);
        window.StartRequested += () =>
        {
            ShowLock();
            window.Close();
        };
        window.ResumeRequested += strictMode =>
        {
            var previousEmergencyMode = state.EmergencyMode;
            var previousStrictMode = state.StrictMode;
            state.EmergencyMode = false;
            state.StrictMode = strictMode;
            try
            {
                store.Save(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.EmergencyMode = previousEmergencyMode;
                state.StrictMode = previousStrictMode;
                window.ShowPersistenceError("保存返回学习状态失败，请检查磁盘或文件权限后重试。");
                return;
            }

            ShowLock();
            window.Close();
        };
        MainWindow = window;
        window.Show();
    }

    void ShowLock()
    {
        EndFocusSession();
        var monitors = MonitorLayout.GetAll();
        var primaryMonitor = monitors.FirstOrDefault(monitor => monitor.IsPrimary);
        if (primaryMonitor.Width <= 0) primaryMonitor = monitors[0];

        var window = new LockWindow(state, store, primaryMonitor);
        lockWindow = window;
        window.Unlocked += _ =>
        {
            EndFocusSession();
            ShowMain();
        };
        MainWindow = window;
        RebuildShieldWindows(monitors, primaryMonitor);
        window.Show();
        window.Activate();

        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        watchdog = FocusWatchdogSession.TryStart();
        if (watchdog is null)
            window.ShowProtectionNotice("自动恢复看门狗未能启动；其他专注保护仍然有效。");
        if (state.StrictMode.Enabled)
        {
            strictModeController = new StrictModeController(window, GetProtectedWindows, state.StrictMode);
            strictModeController.Start();
        }
    }

    IReadOnlyList<Window> GetProtectedWindows()
    {
        var windows = new List<Window>(shieldWindows);
        if (lockWindow is not null) windows.Add(lockWindow);
        return windows;
    }

    void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (lockWindow is null) return;
            var monitors = MonitorLayout.GetAll();
            var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary);
            if (primary.Width <= 0) primary = monitors[0];
            lockWindow.MoveToMonitor(primary);
            RebuildShieldWindows(monitors, primary);
        });
    }

    void RebuildShieldWindows(IReadOnlyList<DisplayMonitor> monitors, DisplayMonitor primary)
    {
        foreach (var shield in shieldWindows) shield.CloseForUnlock();
        shieldWindows.Clear();
        foreach (var monitor in monitors.Where(monitor => monitor != primary))
        {
            var shield = new ShieldWindow(monitor);
            shieldWindows.Add(shield);
            shield.Show();
        }
    }

    void EndFocusSession()
    {
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        strictModeController?.Dispose();
        strictModeController = null;
        watchdog?.Dispose();
        watchdog = null;
        foreach (var shield in shieldWindows) shield.CloseForUnlock();
        shieldWindows.Clear();
        lockWindow = null;
    }

    async Task RefreshExamDate(DateOnly today)
    {
        ExamDateResult? result = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var provider = new ExamDateProvider();
            result = await provider.FetchAsync(today, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
        finally
        {
            var previousDate = state.ExamDate;
            var previousSource = state.ExamDateSource;
            var previousUrl = state.ExamDateUrl;
            var previousUpdatedAt = state.ExamDateUpdatedAt;
            var previousCheckedAt = state.ExamDateCheckedAt;
            var saveFailed = false;
            if (result is not null)
            {
                state.ExamDate = result.Date;
                state.ExamDateSource = "official";
                state.ExamDateUrl = result.Url;
                state.ExamDateUpdatedAt = DateTimeOffset.Now;
            }

            state.ExamDateCheckedAt = DateTimeOffset.Now;
            try
            {
                store.Save(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                saveFailed = true;
                state.ExamDate = previousDate;
                state.ExamDateSource = previousSource;
                state.ExamDateUrl = previousUrl;
                state.ExamDateUpdatedAt = previousUpdatedAt;
                state.ExamDateCheckedAt = previousCheckedAt;
                if (MainWindow is KaoyanFocus.MainWindow failedMain)
                {
                    failedMain.RefreshView();
                    failedMain.ShowPersistenceError("保存考试日期刷新结果失败，请检查磁盘或文件权限后重试。");
                }
            }

            if (!saveFailed && MainWindow is KaoyanFocus.MainWindow main)
                main.RefreshView();
        }
    }
}

public static class SingleInstanceOwnership
{
    const string AcquisitionError = "无法创建单实例保护，可能存在同名系统对象或权限不足。程序将安全退出。";

    public static SingleInstanceAcquisition Acquire(Func<(Mutex Handle, bool OwnsMutex)> create)
    {
        try
        {
            var (handle, ownsMutex) = create();
            return new(true, handle, ownsMutex, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return new(false, null, false, AcquisitionError);
        }
    }

    public static void ReleaseIfOwned(bool ownsMutex, Action release)
    {
        if (ownsMutex) release();
    }
}

public readonly record struct SingleInstanceAcquisition(
    bool Succeeded,
    Mutex? Handle,
    bool OwnsMutex,
    string? ErrorMessage);
