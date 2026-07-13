namespace KaoyanFocus;

public enum DashboardPrimaryAction
{
    Start,
    Resume,
    Completed,
    Unavailable
}

public static class FocusRules
{
    public static int DaysUntil(DateOnly today, DateOnly exam) => exam.DayNumber - today.DayNumber;

    public static bool HasUsableExamDate(AppState state, DateOnly today) =>
        state.ExamDate is { } date && date >= today;

    public static bool ShouldRefreshExamDate(DateTimeOffset? checkedAt, DateTimeOffset now) =>
        checkedAt is null || now - checkedAt >= TimeSpan.FromHours(24);

    public static bool ShouldRefreshExamDate(
        DateOnly? examDate,
        DateTimeOffset? checkedAt,
        DateOnly today,
        DateTimeOffset now) =>
        examDate is { } date && date < today || ShouldRefreshExamDate(checkedAt, now);

    public static bool CanStart(AppState state) =>
        state.Tasks.Count > 0 && state.Tasks.All(t =>
            !string.IsNullOrWhiteSpace(t.Name) && t.TargetSeconds >= 60);

    public static bool CanStartFromDashboard(AppState state) =>
        !state.Started && !AllTasksComplete(state);

    public static DashboardPrimaryAction GetDashboardPrimaryAction(AppState state)
    {
        if (state.EmergencyMode) return DashboardPrimaryAction.Resume;
        if (AllTasksComplete(state)) return DashboardPrimaryAction.Completed;
        return CanStartFromDashboard(state)
            ? DashboardPrimaryAction.Start
            : DashboardPrimaryAction.Unavailable;
    }

    public static bool AllTasksComplete(AppState state) =>
        state.Tasks.Count > 0 && state.Tasks.All(t =>
            t.ElapsedSeconds >= t.TargetSeconds && t.Confirmed);

    public static void AddElapsed(StudyTask task, int seconds)
    {
        if (seconds > 0)
            task.ElapsedSeconds = Math.Min(task.TargetSeconds, task.ElapsedSeconds + seconds);
    }

    public static string FormatDuration(int totalSeconds)
    {
        totalSeconds = Math.Max(0, totalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    public static bool TryActivateTask(AppState state, string taskId)
    {
        var task = state.Tasks.SingleOrDefault(t => t.Id == taskId);
        if (task is null || task.Confirmed) return false;
        state.ActiveTaskId = task.Id;
        return true;
    }

    public static void PauseActiveTask(AppState state) => state.ActiveTaskId = null;

    public static bool TryConfirmActiveTask(AppState state)
    {
        var task = state.ActiveTaskId is null
            ? null
            : state.Tasks.SingleOrDefault(t => t.Id == state.ActiveTaskId);
        if (task is null || task.ElapsedSeconds < task.TargetSeconds) return false;

        task.Confirmed = true;
        PauseActiveTask(state);
        if (AllTasksComplete(state)) state.Started = false;
        return true;
    }

    public static bool TryUseEmergency(AppState state)
    {
        if (state.EmergencyUses >= 2) return false;
        state.EmergencyUses++;
        state.EmergencyMode = true;
        state.ActiveTaskId = null;
        return true;
    }

    public static void RollTo(AppState state, DateOnly day)
    {
        if (state.Day == day) return;
        if (state.Tasks.Count > 0)
        {
            state.Archive.Add(new DailyRecord
            {
                Day = state.Day,
                Tasks = state.Tasks.Select(t => new StudyTask
                {
                    Id = t.Id,
                    Name = t.Name,
                    TargetSeconds = t.TargetSeconds,
                    ElapsedSeconds = t.ElapsedSeconds,
                    Confirmed = t.Confirmed
                }).ToList()
            });
        }
        state.Day = day;
        state.Tasks.Clear();
        state.Started = false;
        state.EmergencyMode = false;
        state.EmergencyUses = 0;
        state.ActiveTaskId = null;
    }
}

public readonly record struct ExamDateDashboardDescription(
    string DaysText,
    string SourceText,
    bool ShowManualDate);

public static class ExamDateDashboard
{
    public static ExamDateDashboardDescription Describe(AppState state, DateOnly today)
    {
        var expired = state.ExamDate is { } examDate && examDate < today;
        if (expired)
        {
            return new(
                "待设置",
                state.ExamDateCheckedAt is not null
                    ? "官方日期已过期且刷新失败，请设置手动备用日期"
                    : "官方日期已过期，正在刷新；也可设置手动备用日期",
                true);
        }

        var daysText = state.ExamDate is { } date
            ? $"{FocusRules.DaysUntil(today, date)} 天"
            : "待设置";
        var sourceText = state.ExamDateSource switch
        {
            "official" => "日期来源：教育部官方公告",
            "manual" => "日期来源：手动备用",
            _ when state.ExamDateCheckedAt is not null => "官方日期获取失败，请设置手动备用日期",
            _ => "正在获取官方考试日期"
        };
        return new(daysText, sourceText, state.ExamDate is null);
    }
}

public static class LockTaskSwitch
{
    public static bool TrySwitch(
        AppState state,
        string taskId,
        Action flushElapsed,
        Action stopTiming,
        Action resetBaseline,
        Func<bool> trySave,
        Action<bool> restoreTiming,
        Action startNewTiming)
    {
        var target = state.Tasks.SingleOrDefault(task => task.Id == taskId);
        if (target is null || target.Confirmed) return false;

        var previousTaskId = state.ActiveTaskId;
        var wasActive = previousTaskId is not null;
        flushElapsed();
        stopTiming();
        resetBaseline();
        state.ActiveTaskId = target.Id;

        if (!trySave())
        {
            state.ActiveTaskId = previousTaskId;
            restoreTiming(wasActive);
            return false;
        }

        startNewTiming();
        return true;
    }
}

public static class LockModalPause
{
    public static T Run<T>(Action stopTiming, Func<T> showModal, Action restoreTiming)
    {
        stopTiming();
        try
        {
            return showModal();
        }
        finally
        {
            restoreTiming();
        }
    }
}

public static class DashboardDayTransition
{
    public static bool TryRoll(AppState state, DateOnly today, Action<AppState> save)
    {
        if (state.Day == today) return false;

        var snapshot = AppStateSnapshot.Capture(state);
        FocusRules.RollTo(state, today);
        try
        {
            save(state);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            snapshot.Restore(state);
            return false;
        }
    }

    private sealed record AppStateSnapshot(
        DateOnly Day,
        List<StudyTask> Tasks,
        List<DailyRecord> Archive,
        bool Started,
        bool EmergencyMode,
        int EmergencyUses,
        string? ActiveTaskId)
    {
        public static AppStateSnapshot Capture(AppState state) => new(
            state.Day, state.Tasks.Select(CloneTask).ToList(),
            state.Archive.Select(record => new DailyRecord
            {
                Day = record.Day,
                Tasks = record.Tasks.Select(CloneTask).ToList()
            }).ToList(), state.Started,
            state.EmergencyMode, state.EmergencyUses, state.ActiveTaskId);

        static StudyTask CloneTask(StudyTask task) => new()
        {
            Id = task.Id,
            Name = task.Name,
            TargetSeconds = task.TargetSeconds,
            ElapsedSeconds = task.ElapsedSeconds,
            Confirmed = task.Confirmed
        };

        public void Restore(AppState state)
        {
            state.Day = Day;
            state.Tasks = Tasks;
            state.Archive = Archive;
            state.Started = Started;
            state.EmergencyMode = EmergencyMode;
            state.EmergencyUses = EmergencyUses;
            state.ActiveTaskId = ActiveTaskId;
        }
    }
}
