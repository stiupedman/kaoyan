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

    public static bool ShouldRefreshExamDate(DateTimeOffset? checkedAt, DateTimeOffset now) =>
        checkedAt is null || now - checkedAt >= TimeSpan.FromHours(24);

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
