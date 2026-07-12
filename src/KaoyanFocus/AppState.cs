namespace KaoyanFocus;

public sealed class AppState
{
    public DateOnly Day { get; set; }
    public DateOnly? ExamDate { get; set; }
    public string ExamDateSource { get; set; } = "none";
    public string? ExamDateUrl { get; set; }
    public DateTimeOffset? ExamDateUpdatedAt { get; set; }
    public DateTimeOffset? ExamDateCheckedAt { get; set; }
    public List<StudyTask> Tasks { get; set; } = [];
    public List<DailyRecord> Archive { get; set; } = [];
    public bool Started { get; set; }
    public bool EmergencyMode { get; set; }
    public int EmergencyUses { get; set; }
    public string? ActiveTaskId { get; set; }
    public bool RecoveryWarning { get; set; }

    public static AppState NewDay(DateOnly day) => new() { Day = day };
}

public sealed class StudyTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int TargetSeconds { get; set; }
    public int ElapsedSeconds { get; set; }
    public bool Confirmed { get; set; }
}

public sealed class DailyRecord
{
    public DateOnly Day { get; set; }
    public List<StudyTask> Tasks { get; set; } = [];
}
