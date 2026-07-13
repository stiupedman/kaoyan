using System.IO;
using System.Net.Http;
using System.Windows;

namespace KaoyanFocus;

public partial class App : Application
{
    readonly StateStore store = new(StateStore.DefaultPath);
    AppState state = null!;
    Mutex? instanceMutex;
    bool ownsInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instanceMutex = new Mutex(true, "KaoyanFocus.SingleInstance", out ownsInstanceMutex);
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
        if (FocusRules.ShouldRefreshExamDate(state.ExamDateCheckedAt, DateTimeOffset.Now))
            await RefreshExamDate(today);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstanceOwnership.ReleaseIfOwned(
            ownsInstanceMutex,
            () => instanceMutex?.ReleaseMutex());
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    void ShowMain()
    {
        var window = new MainWindow(state, store);
        window.StartRequested += () =>
        {
            ShowLock();
            window.Close();
        };
        window.ResumeRequested += () =>
        {
            var previousEmergencyMode = state.EmergencyMode;
            state.EmergencyMode = false;
            try
            {
                store.Save(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.EmergencyMode = previousEmergencyMode;
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
        var window = new LockWindow(state, store);
        window.Unlocked += () =>
        {
            ShowMain();
            window.Close();
        };
        MainWindow = window;
        window.Show();
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
    public static void ReleaseIfOwned(bool ownsMutex, Action release)
    {
        if (ownsMutex) release();
    }
}
