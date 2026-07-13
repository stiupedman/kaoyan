using System.Collections.ObjectModel;
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
        DaysText.Text = state.ExamDate is { } date
            ? $"{FocusRules.DaysUntil(DateOnly.FromDateTime(DateTime.Today), date)} 天"
            : "待设置";
        DateSourceText.Text = state.ExamDateSource switch
        {
            "official" => "日期来源：教育部官方公告",
            "manual" => "日期来源：手动备用",
            _ => "正在获取官方考试日期"
        };
        EmergencyText.Text = $"今日剩余应急解锁：{Math.Max(0, 2 - state.EmergencyUses)} 次";
        ManualDatePanel.Visibility = state.ExamDate is null ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Content = state.EmergencyMode ? "返回学习" : "开始今日学习";
        TaskList.IsEnabled = !state.Started;
        AddTaskButton.IsEnabled = !state.Started;
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
        if (state.EmergencyMode)
        {
            ResumeRequested?.Invoke();
            return;
        }

        if (state.ExamDate is null)
        {
            ErrorText.Text = "请先等待官方日期获取，或设置备用考试日期。";
            return;
        }

        state.Tasks = rows.Select(row => row.ToTask()).ToList();
        if (!FocusRules.CanStart(state))
        {
            ErrorText.Text = "请至少添加一项名称非空、时长不少于 1 分钟的任务。";
            return;
        }

        state.Started = true;
        store.Save(state);
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

        state.ExamDate = date;
        state.ExamDateSource = "manual";
        store.Save(state);
        ErrorText.Text = "";
        RefreshView();
    }
}

public sealed class TaskRow(StudyTask task)
{
    public string Name { get; set; } = task.Name;
    public int TargetMinutes { get; set; } = Math.Max(1, task.TargetSeconds / 60);

    public StudyTask ToTask() => new()
    {
        Id = task.Id,
        Name = Name.Trim(),
        TargetSeconds = TargetMinutes * 60
    };
}
