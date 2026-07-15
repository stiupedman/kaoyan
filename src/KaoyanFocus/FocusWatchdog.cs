using System.Diagnostics;
using System.IO;

namespace KaoyanFocus;

public readonly record struct WatchdogCommand(int ParentProcessId, string StopEventName)
{
    public const string Switch = "--focus-watchdog";

    public static bool TryParse(IReadOnlyList<string> arguments, out WatchdogCommand command)
    {
        command = default;
        if (arguments.Count != 3 ||
            !string.Equals(arguments[0], Switch, StringComparison.Ordinal) ||
            !int.TryParse(arguments[1], out var processId) || processId <= 0 ||
            string.IsNullOrWhiteSpace(arguments[2]) ||
            !arguments[2].StartsWith("Local\\KaoyanFocus.Watchdog.", StringComparison.Ordinal))
            return false;

        command = new(processId, arguments[2]);
        return true;
    }
}

public sealed class FocusWatchdogSession : IDisposable
{
    readonly EventWaitHandle stopEvent;
    readonly Process process;
    bool stopped;

    FocusWatchdogSession(EventWaitHandle stopEvent, Process process)
    {
        this.stopEvent = stopEvent;
        this.process = process;
    }

    public static FocusWatchdogSession? TryStart()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return null;

        var eventName = $"Local\\KaoyanFocus.Watchdog.{Guid.NewGuid():N}";
        var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        try
        {
            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(WatchdogCommand.Switch);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(eventName);
            var process = Process.Start(start);
            if (process is null)
            {
                stopEvent.Dispose();
                return null;
            }
            return new(stopEvent, process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            stopEvent.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        stopEvent.Set();
        if (!process.HasExited) process.WaitForExit(1000);
        process.Dispose();
        stopEvent.Dispose();
    }
}

public static class FocusWatchdogHost
{
    public static async Task RunAsync(WatchdogCommand command)
    {
        Process? parent = null;
        EventWaitHandle? stopEvent = null;
        try
        {
            try
            {
                parent = Process.GetProcessById(command.ParentProcessId);
                stopEvent = EventWaitHandle.OpenExisting(command.StopEventName);
            }
            catch (Exception ex) when (ex is ArgumentException or WaitHandleCannotBeOpenedException)
            {
                await RestartAsync();
                return;
            }

            var stoppedNormally = await Task.Run(() =>
            {
                while (true)
                {
                    if (stopEvent.WaitOne(500)) return true;
                    try
                    {
                        if (parent.HasExited) return false;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }
            });
            if (!stoppedNormally) await RestartAsync();
        }
        finally
        {
            stopEvent?.Dispose();
            parent?.Dispose();
        }
    }

    static async Task RestartAsync()
    {
        ShellTaskbarController.ShowAll();
        await Task.Delay(300);

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return;
        try
        {
            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false
            };
            start.ArgumentList.Add("--recovered-by-watchdog");
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShellTaskbarController.ShowAll();
        }
    }
}
