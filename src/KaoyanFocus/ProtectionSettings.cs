using System.Globalization;
using System.IO;

namespace KaoyanFocus;

public static class ProtectionSettings
{
    public static readonly string[] DefaultBlockedProcesses =
    [
        "YuanShen",
        "GenshinImpact",
        "StarRail",
        "ZenlessZoneZero",
        "HYP"
    ];

    public static bool TryCreate(
        bool enabled,
        string idleMinutesText,
        string blockedProcessesText,
        out StrictModeSettings settings)
    {
        settings = null!;
        if (!int.TryParse(idleMinutesText, NumberStyles.None, CultureInfo.InvariantCulture, out var idleMinutes) ||
            idleMinutes is < 1 or > 120)
            return false;

        var blocked = ParseBlockedProcesses(blockedProcessesText);
        if (blocked.Count > 64) return false;

        settings = new StrictModeSettings
        {
            Enabled = enabled,
            IdleTimeoutMinutes = idleMinutes,
            BlockedProcesses = blocked
        };
        return true;
    }

    public static List<string> ParseBlockedProcesses(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split([',', ';', '，', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = raw.Trim().Trim('"');
            if (value.Length == 0) continue;

            try
            {
                value = Path.GetFileNameWithoutExtension(value);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (value.Length is 0 or > 128 || !seen.Add(value)) continue;
            result.Add(value);
        }

        return result;
    }

    public static string ToEditorText(StrictModeSettings settings) =>
        string.Join(", ", settings.BlockedProcesses);

    public static bool BlocksProcess(StrictModeSettings settings, string processName) =>
        settings.Enabled && settings.BlockedProcesses.Contains(
            Path.GetFileNameWithoutExtension(processName), StringComparer.OrdinalIgnoreCase);
}

public static class IdlePolicy
{
    public static bool ShouldPause(ulong idleMilliseconds, int timeoutMinutes)
    {
        if (timeoutMinutes is < 1 or > 120) return false;
        return idleMilliseconds >= (ulong)timeoutMinutes * 60_000UL;
    }
}
