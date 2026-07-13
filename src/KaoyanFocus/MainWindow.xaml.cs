using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KaoyanFocus;

public partial class MainWindow : Window
{
    readonly AppState state;
    readonly StateStore store;
    readonly ObservableCollection<TaskRow> rows = [];

    public event Action? StartRequested;
    public event Action? ResumeRequested;

    public MainWindow(AppState state, StateStore store)
    {
        InitializeComponent();
        this.state = state;
        this.store = store;
        foreach (var task in state.Tasks)
            rows.Add(new TaskRow(task));
        TaskList.ItemsSource = rows;
        RefreshView();
    }

    public void RefreshView()
    {
        var completed = FocusRules.AllTasksComplete(state);
        var primaryAction = FocusRules.GetDashboardPrimaryAction(state);
        DaysText.Text = state.ExamDate is { } date
            ? $"{FocusRules.DaysUntil(DateOnly.FromDateTime(DateTime.Today), date)} 天"
            : "待设置";
        DateSourceText.Text = state.ExamDateSource switch
        {
            "official" => "日期来源：教育部官方公告",
            "manual" => "日期来源：手动备用",
            _ when state.ExamDateCheckedAt is not null => "官方日期获取失败，请设置手动备用日期",
            _ => "正在获取官方考试日期"
        };
        EmergencyText.Text = $"今日剩余应急解锁：{Math.Max(0, 2 - state.EmergencyUses)} 次";
        ManualDatePanel.Visibility = state.ExamDate is null ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Content = primaryAction switch
        {
            DashboardPrimaryAction.Resume => "返回学习",
            DashboardPrimaryAction.Completed => "今日任务已完成",
            _ => "开始今日学习"
        };
        PrimaryButton.IsEnabled = primaryAction is DashboardPrimaryAction.Start or DashboardPrimaryAction.Resume;
        TaskList.IsEnabled = !state.Started && !completed;
        AddTaskButton.IsEnabled = !state.Started && !completed;
        if (state.RecoveryWarning)
            ErrorText.Text = "检测到损坏的数据文件，原文件已备份；请重新设置今天的任务。";
    }

    void AddTask_Click(object sender, RoutedEventArgs e) => rows.Add(new TaskRow(new StudyTask()));

    void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TaskRow row })
            rows.Remove(row);
    }

    void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        var primaryAction = FocusRules.GetDashboardPrimaryAction(state);
        if (primaryAction == DashboardPrimaryAction.Resume)
        {
            ResumeRequested?.Invoke();
            return;
        }

        if (primaryAction != DashboardPrimaryAction.Start)
        {
            ErrorText.Text = primaryAction == DashboardPrimaryAction.Completed
                ? "今日任务已完成，明天再开始新的学习计划。"
                : "今日学习已经开始。";
            return;
        }

        if (state.ExamDate is null)
        {
            ErrorText.Text = "请先等待官方日期获取，或设置备用考试日期。";
            return;
        }

        if (!TaskSetup.TryBuildTasks(rows, out var candidates))
        {
            ErrorText.Text = "请检查每项任务：名称不能为空，时长须为不少于 1 的整数分钟。";
            return;
        }

        var previousTasks = state.Tasks;
        var previousStarted = state.Started;
        state.Tasks = candidates;
        state.Started = true;
        try
        {
            store.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.Tasks = previousTasks;
            state.Started = previousStarted;
            ShowPersistenceError("保存今日任务失败，请检查磁盘或文件权限后重试。");
            return;
        }

        StartRequested?.Invoke();
    }

    void ManualDateChanged(object sender, RoutedEventArgs e)
    {
        if (ManualDatePicker.SelectedDate is not { } value)
            return;

        var date = DateOnly.FromDateTime(value);
        if (date < DateOnly.FromDateTime(DateTime.Today))
        {
            ErrorText.Text = "考试日期不能早于今天。";
            return;
        }

        var previousDate = state.ExamDate;
        var previousSource = state.ExamDateSource;
        state.ExamDate = date;
        state.ExamDateSource = "manual";
        try
        {
            store.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.ExamDate = previousDate;
            state.ExamDateSource = previousSource;
            ShowPersistenceError("保存备用考试日期失败，请检查磁盘或文件权限后重试。");
            return;
        }

        ErrorText.Text = "";
        RefreshView();
    }

    public void ShowPersistenceError(string message) => ErrorText.Text = message;
}

public sealed class TaskRow(StudyTask task)
{
    public string Name { get; set; } = task.Name;
    public string TargetMinutesText { get; set; } = Math.Max(1, task.TargetSeconds / 60).ToString(CultureInfo.InvariantCulture);

    public bool TryCreateTask(out StudyTask candidate)
    {
        candidate = null!;
        if (string.IsNullOrWhiteSpace(Name) ||
            !int.TryParse(TargetMinutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            minutes < 1 || minutes > int.MaxValue / 60)
            return false;

        candidate = new StudyTask
        {
            Id = task.Id,
            Name = Name.Trim(),
            TargetSeconds = minutes * 60,
            ElapsedSeconds = task.ElapsedSeconds,
            Confirmed = task.Confirmed
        };
        return true;
    }
}

public static class TaskSetup
{
    public static bool TryBuildTasks(IEnumerable<TaskRow> rows, out List<StudyTask> candidates)
    {
        candidates = [];
        foreach (var row in rows)
        {
            if (!row.TryCreateTask(out var candidate))
            {
                candidates = [];
                return false;
            }

            candidates.Add(candidate);
        }

        return candidates.Count > 0;
    }
}
