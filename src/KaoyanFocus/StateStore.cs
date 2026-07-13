using System.IO;
using System.Text.Json;

namespace KaoyanFocus;

public sealed class StateStore(string path)
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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
            FocusRules.RollTo(state, today);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var backup = Path.Combine(Path.GetDirectoryName(path)!,
                $"state.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Move(path, backup, true);
            var state = AppState.NewDay(today);
            state.RecoveryWarning = true;
            return state;
        }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, path, true);
    }
}
