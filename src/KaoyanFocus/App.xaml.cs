using System.Net.Http;
using System.Windows;

namespace KaoyanFocus;

public partial class App : Application
{
    readonly StateStore store = new(StateStore.DefaultPath);
    AppState state = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
            state.EmergencyMode = false;
            store.Save(state);
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
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var provider = new ExamDateProvider();
            var result = await provider.FetchAsync(today, timeout.Token);
            if (result is not null)
            {
                state.ExamDate = result.Date;
                state.ExamDateSource = "official";
                state.ExamDateUrl = result.Url;
                state.ExamDateUpdatedAt = DateTimeOffset.Now;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
        finally
        {
            state.ExamDateCheckedAt = DateTimeOffset.Now;
            store.Save(state);
            if (MainWindow is KaoyanFocus.MainWindow main)
                main.RefreshView();
        }
    }
}
