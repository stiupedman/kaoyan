using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KaoyanFocus;

public partial class LockWindow : Window
{
    readonly AppState state;
    readonly StateStore store;
    readonly Stopwatch stopwatch = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool allowClose;
    bool persistenceWarningShown;
    int savedWholeSeconds;
    int ticks;

    public event Action? Unlocked;

    public LockWindow(AppState state, StateStore store)
    {
        InitializeComponent();
        this.state = state;
        this.store = store;
        FocusRules.PauseActiveTask(state);
        TaskList.ItemsSource = state.Tasks.Select(task => new LockTaskRow(task)).ToList();
        timer.Tick += Timer_Tick;
        timer.Start();
        SystemEvents.PowerModeChanged += PowerModeChanged;
        Closed += Window_Closed;
        RefreshView();
    }

    void Task_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LockTaskRow row }) return;

        FlushElapsed();
        var previousTaskId = state.ActiveTaskId;
        if (!FocusRules.TryActivateTask(state, row.Task.Id)) return;

        stopwatch.Restart();
        savedWholeSeconds = 0;
        if (!TrySave())
        {
            state.ActiveTaskId = previousTaskId;
            if (previousTaskId is null) stopwatch.Reset();
            else stopwatch.Restart();
            RefreshView();
            return;
        }

        RefreshView();
    }

    void Timer_Tick(object? sender, EventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today != state.Day)
        {
            FlushElapsed();
            FocusRules.RollTo(state, today);
            ExitToMain();
            return;
        }

        if (!state.Started || FocusRules.AllTasksComplete(state))
        {
            ExitToMain();
            return;
        }

        FlushElapsed();
        if (++ticks % 5 == 0) TrySave();
        RefreshView();
    }

    void FlushElapsed()
    {
        var task = ActiveTask();
        if (task is null) return;

        var wholeSeconds = (int)stopwatch.Elapsed.TotalSeconds;
        FocusRules.AddElapsed(task, wholeSeconds - savedWholeSeconds);
        savedWholeSeconds = wholeSeconds;
    }

    void Confirm_Click(object sender, RoutedEventArgs e)
    {
        FlushElapsed();
        if (!FocusRules.TryConfirmActiveTask(state)) return;

        stopwatch.Reset();
        savedWholeSeconds = 0;
        if (FocusRules.AllTasksComplete(state))
        {
            ExitToMain();
            return;
        }

        TrySave();
        RefreshView();
    }

    void Emergency_Click(object sender, RoutedEventArgs e)
    {
        var remaining = 2 - state.EmergencyUses;
        if (remaining <= 0) return;

        var answer = MessageBox.Show(
            $"本次使用后，今天还剩 {remaining - 1} 次应急解锁。确认使用吗？",
            "应急解锁", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        FlushElapsed();
        var previousUses = state.EmergencyUses;
        var previousMode = state.EmergencyMode;
        var previousTaskId = state.ActiveTaskId;
        if (!FocusRules.TryUseEmergency(state)) return;

        if (!TrySave())
        {
            state.EmergencyUses = previousUses;
            state.EmergencyMode = previousMode;
            state.ActiveTaskId = previousTaskId;
            if (previousTaskId is not null)
            {
                stopwatch.Restart();
                savedWholeSeconds = 0;
            }
            RefreshView();
            return;
        }

        CompleteExit();
    }

    void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Suspend) return;
        Dispatcher.Invoke(PauseForSuspend);
    }

    void PauseForSuspend()
    {
        FlushElapsed();
        stopwatch.Reset();
        savedWholeSeconds = 0;
        FocusRules.PauseActiveTask(state);
        TrySave();
        RefreshView();
    }

    StudyTask? ActiveTask() => state.ActiveTaskId is null
        ? null
        : state.Tasks.SingleOrDefault(task => task.Id == state.ActiveTaskId);

    void RefreshView()
    {
        var active = ActiveTask();
        DaysText.Text = state.ExamDate is { } exam
            ? $"{FocusRules.DaysUntil(DateOnly.FromDateTime(DateTime.Today), exam)} 天"
            : "待设置";
        CurrentTaskText.Text = active?.Name ?? "请选择一项任务";
        var remaining = active is null ? 0 : Math.Max(0, active.TargetSeconds - active.ElapsedSeconds);
        TimerText.Text = TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm\:ss");
        ConfirmButton.IsEnabled = active is not null && active.ElapsedSeconds >= active.TargetSeconds;
        EmergencyButton.Content = $"应急解锁（剩余 {Math.Max(0, 2 - state.EmergencyUses)} 次）";
        EmergencyButton.IsEnabled = state.EmergencyUses < 2;
        TaskList.Items.Refresh();
    }

    bool TrySave()
    {
        FlushElapsed();
        try
        {
            store.Save(state);
            persistenceWarningShown = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!persistenceWarningShown)
            {
                persistenceWarningShown = true;
                MessageBox.Show(
                    "保存学习进度失败。专注窗口将保持打开，请检查磁盘空间或文件权限后重试。",
                    "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
    }

    void ExitToMain()
    {
        FlushElapsed();
        stopwatch.Stop();
        if (!TrySave())
        {
            if (state.ActiveTaskId is not null) stopwatch.Start();
            return;
        }

        CompleteExit();
    }

    void CompleteExit()
    {
        stopwatch.Stop();
        timer.Stop();
        allowClose = true;
        Unlocked?.Invoke();
        if (IsVisible) Close();
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        FlushElapsed();
        TrySave();
        if (!allowClose) e.Cancel = true;
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        timer.Stop();
        SystemEvents.PowerModeChanged -= PowerModeChanged;
    }
}

public sealed class LockTaskRow(StudyTask task)
{
    public StudyTask Task { get; } = task;
    public string Name => (Task.Confirmed ? "✓ " : "") + Task.Name;
    public string ProgressText => $"{Task.ElapsedSeconds / 60} / {Task.TargetSeconds / 60} 分钟";
}
