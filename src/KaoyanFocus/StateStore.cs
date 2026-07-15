using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace KaoyanFocus;

public sealed class StateStore
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    readonly string path;

    public StateStore(string path) => this.path = Path.GetFullPath(path);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KaoyanFocus", "state.json");

    public AppState LoadOrCreate(DateOnly today)
    {
        if (!File.Exists(path)) return AppState.NewDay(today);
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("Empty state");
            StateStructure.Validate(state);
            FocusRules.RollTo(state, today);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var backup = Path.Combine(directory,
                $"{name}.corrupt-{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
            File.Move(path, backup, false);
            var state = AppState.NewDay(today);
            state.RecoveryWarning = true;
            return state;
        }
    }

    public void Save(AppState state)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, path, true);
    }
}

static class StateStructure
{
    public static void Validate(AppState state)
    {
        if (state.Tasks is null || state.Archive is null || state.StrictMode is null)
            Invalid("Task collections cannot be null.");
        if (state.EmergencyUses is < 0 or > 2)
            Invalid("Emergency uses are outside the daily limit.");

        ValidateTasks(state.Tasks, "active tasks");
        foreach (var record in state.Archive)
        {
            if (record is null || record.Tasks is null)
                Invalid("Archive records cannot be null.");
            ValidateTasks(record.Tasks, "archive tasks");
        }

        if (state.Started && state.Tasks.Count == 0)
            Invalid("A started session requires tasks.");
        if (state.ActiveTaskId is not null && state.Tasks.All(task => task.Id != state.ActiveTaskId))
            Invalid("The active task must exist.");
        if (state.StrictMode.IdleTimeoutMinutes is < 1 or > 120 ||
            state.StrictMode.BlockedProcesses is null || state.StrictMode.BlockedProcesses.Count > 64 ||
            state.StrictMode.BlockedProcesses.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128))
            Invalid("Strict mode settings are invalid.");
    }

    static void ValidateTasks(IEnumerable<StudyTask> tasks, string context)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Id) || !ids.Add(task.Id))
                Invalid($"{context} contain an invalid task id.");
            if (string.IsNullOrWhiteSpace(task.Name) || task.TargetSeconds < 60)
                Invalid($"{context} contain an invalid task definition.");
            if (task.ElapsedSeconds < 0 || task.ElapsedSeconds > task.TargetSeconds)
                Invalid($"{context} contain invalid elapsed time.");
            if (task.Confirmed && task.ElapsedSeconds < task.TargetSeconds)
                Invalid($"{context} contain an early confirmation.");
        }
    }

    [DoesNotReturn]
    static void Invalid(string message) => throw new InvalidDataException(message);
}
