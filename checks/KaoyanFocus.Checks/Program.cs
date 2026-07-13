using KaoyanFocus;

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{name}: expected {expected}, got {actual}");
}

var today = new DateOnly(2026, 7, 12);
Equal(160, FocusRules.DaysUntil(today, new DateOnly(2026, 12, 19)), "countdown");
Equal(0, FocusRules.DaysUntil(today, today), "exam day");

var state = AppState.NewDay(today);
state.Tasks.Add(new StudyTask { Name = "高数", TargetSeconds = 60, ElapsedSeconds = 60 });
Equal(false, FocusRules.AllTasksComplete(state), "confirmation required");
state.Tasks[0].Confirmed = true;
Equal(true, FocusRules.AllTasksComplete(state), "time and confirmation");
Equal(true, FocusRules.TryUseEmergency(state), "emergency one");
Equal(true, FocusRules.TryUseEmergency(state), "emergency two");
Equal(false, FocusRules.TryUseEmergency(state), "emergency limit");

FocusRules.RollTo(state, today.AddDays(1));
Equal(0, state.EmergencyUses, "daily reset");
Equal(0, state.Tasks.Count, "old tasks archived from active day");
Equal(1, state.Archive.Count, "archive retained");

var temp = Path.Combine(Path.GetTempPath(), "KaoyanFocusChecks", Guid.NewGuid().ToString("N"));
var store = new StateStore(Path.Combine(temp, "state.json"));
store.Save(state);
var loaded = store.LoadOrCreate(state.Day);
Equal(state.Day, loaded.Day, "saved day");
Equal(state.EmergencyUses, loaded.EmergencyUses, "saved emergency count");

File.WriteAllText(Path.Combine(temp, "state.json"), "not-json");
var recovered = store.LoadOrCreate(state.Day);
Equal(true, recovered.RecoveryWarning, "corrupt state warning");
Equal(true, Directory.GetFiles(temp, "state.corrupt-*.json").Length == 1, "corrupt backup");
Console.WriteLine("All checks passed.");
