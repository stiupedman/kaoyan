using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace KaoyanFocus;

public readonly record struct DisplayMonitor(int Left, int Top, int Right, int Bottom, bool IsPrimary)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class MonitorLayout
{
    public static IReadOnlyList<DisplayMonitor> GetAll()
    {
        var monitors = new List<DisplayMonitor>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MonitorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
            };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new(
                    info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right, info.Monitor.Bottom,
                    (info.Flags & NativeMethods.MonitorPrimary) != 0));
            }
            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            monitors.Add(new(
                0, 0,
                NativeMethods.GetSystemMetrics(NativeMethods.ScreenWidth),
                NativeMethods.GetSystemMetrics(NativeMethods.ScreenHeight),
                true));
        }

        return monitors.OrderByDescending(monitor => monitor.IsPrimary).ToList();
    }
}

public static class WindowProtection
{
    public static void FillMonitor(Window window, DisplayMonitor monitor, bool activate)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            handle, NativeMethods.Topmost,
            monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            NativeMethods.ShowWindow | NativeMethods.FrameChanged |
            (activate ? 0u : NativeMethods.NoActivate));
    }

    public static void ReassertTopmost(Window window, bool activate)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            handle, NativeMethods.Topmost, 0, 0, 0, 0,
            NativeMethods.NoMove | NativeMethods.NoSize | NativeMethods.ShowWindow |
            (activate ? 0u : NativeMethods.NoActivate));
        if (activate)
        {
            window.Activate();
            NativeMethods.SetForegroundWindow(handle);
        }
    }

    public static bool IsAnotherProcessInForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId != (uint)Environment.ProcessId;
    }
}

public sealed class ShellTaskbarController : IDisposable
{
    public void HideAll() => SetVisibility(NativeMethods.Hide);

    public void Dispose() => ShowAll();

    public static void ShowAll() => SetVisibility(NativeMethods.Show);

    static void SetVisibility(int command)
    {
        var primary = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) NativeMethods.ShowWindowCommand(primary, command);

        var cursor = IntPtr.Zero;
        while (true)
        {
            cursor = NativeMethods.FindWindowEx(IntPtr.Zero, cursor, "Shell_SecondaryTrayWnd", null);
            if (cursor == IntPtr.Zero) break;
            NativeMethods.ShowWindowCommand(cursor, command);
        }
    }
}

public static class SystemShortcutPolicy
{
    public static bool ShouldBlock(uint virtualKey, bool alt, bool control, bool shift) =>
        virtualKey is NativeMethods.LeftWindows or NativeMethods.RightWindows ||
        alt && virtualKey is NativeMethods.Tab or NativeMethods.Escape or NativeMethods.F4 ||
        control && virtualKey == NativeMethods.Escape ||
        control && shift && virtualKey == NativeMethods.Escape;
}

public sealed class KeyboardShield : IDisposable
{
    readonly NativeMethods.LowLevelKeyboardProcedure callback;
    IntPtr hook;

    public KeyboardShield() => callback = HookCallback;

    public bool Start()
    {
        if (hook != IntPtr.Zero) return true;
        hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.KeyboardLowLevel, callback,
            NativeMethods.GetModuleHandle(null), 0);
        return hook != IntPtr.Zero;
    }

    IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message is var value &&
            (value == (IntPtr)NativeMethods.KeyDown || value == (IntPtr)NativeMethods.SystemKeyDown ||
             value == (IntPtr)NativeMethods.KeyUp || value == (IntPtr)NativeMethods.SystemKeyUp))
        {
            var key = Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardInput>(data).VirtualKey;
            var alt = (NativeMethods.GetAsyncKeyState(NativeMethods.Menu) & 0x8000) != 0;
            var control = (NativeMethods.GetAsyncKeyState(NativeMethods.Control) & 0x8000) != 0;
            var shift = (NativeMethods.GetAsyncKeyState(NativeMethods.Shift) & 0x8000) != 0;
            if (SystemShortcutPolicy.ShouldBlock(key, alt, control, shift)) return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        if (hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }
}

public static class UserIdleTime
{
    public static ulong GetMilliseconds()
    {
        var info = new NativeMethods.LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>()
        };
        if (!NativeMethods.GetLastInputInfo(ref info)) return 0;
        var currentTick = unchecked((uint)NativeMethods.GetTickCount64());
        return unchecked(currentTick - info.Time);
    }
}

public sealed class BlockedProcessEnforcer
{
    readonly HashSet<int> reported = [];

    public IReadOnlyList<string> MinimizeBlocked(StrictModeSettings settings)
    {
        var minimized = new List<string>();
        Process[] running;
        try
        {
            running = Process.GetProcesses();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return minimized;
        }

        foreach (var process in running)
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId ||
                        !ProtectionSettings.BlocksProcess(settings, process.ProcessName))
                        continue;

                    process.Refresh();
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    NativeMethods.ShowWindowCommand(handle, NativeMethods.Minimize);
                    if (reported.Add(process.Id)) minimized.Add(process.ProcessName);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
            }
        }
        return minimized;
    }
}

public sealed class StrictModeController : IDisposable
{
    readonly LockWindow primaryWindow;
    readonly Func<IReadOnlyList<Window>> protectedWindows;
    readonly StrictModeSettings settings;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly KeyboardShield keyboard = new();
    readonly ShellTaskbarController taskbar = new();
    readonly BlockedProcessEnforcer processes = new();
    int ticks;

    public StrictModeController(
        LockWindow primaryWindow,
        Func<IReadOnlyList<Window>> protectedWindows,
        StrictModeSettings settings)
    {
        this.primaryWindow = primaryWindow;
        this.protectedWindows = protectedWindows;
        this.settings = settings.Clone();
        timer.Tick += Timer_Tick;
    }

    public void Start()
    {
        if (!keyboard.Start())
            primaryWindow.ShowProtectionNotice("系统快捷键限制未能启动；窗口与任务栏保护仍然有效。");
        timer.Start();
        Timer_Tick(null, EventArgs.Empty);
    }

    void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            Enforce();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            primaryWindow.ShowProtectionNotice("强化模式暂时无法控制一个系统窗口，将继续重试。");
        }
    }

    void Enforce()
    {
        taskbar.HideAll();
        var windows = protectedWindows();
        foreach (var window in windows.Where(window => !ReferenceEquals(window, primaryWindow)))
            WindowProtection.ReassertTopmost(window, false);

        var foregroundChanged = WindowProtection.IsAnotherProcessInForeground();
        WindowProtection.ReassertTopmost(primaryWindow, foregroundChanged);

        var shouldPause = IdlePolicy.ShouldPause(UserIdleTime.GetMilliseconds(), settings.IdleTimeoutMinutes);
        primaryWindow.SetIdlePaused(shouldPause);

        if (++ticks % 2 == 0)
        {
            var minimized = processes.MinimizeBlocked(settings);
            if (minimized.Count > 0)
                primaryWindow.ShowProtectionNotice($"已最小化黑名单程序：{string.Join("、", minimized)}");
        }
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= Timer_Tick;
        keyboard.Dispose();
        taskbar.Dispose();
    }
}

internal static class NativeMethods
{
    public const uint MonitorPrimary = 1;
    public const int ScreenWidth = 0;
    public const int ScreenHeight = 1;
    public const uint NoSize = 0x0001;
    public const uint NoMove = 0x0002;
    public const uint NoActivate = 0x0010;
    public const uint FrameChanged = 0x0020;
    public const uint ShowWindow = 0x0040;
    public const int Hide = 0;
    public const int Show = 5;
    public const int Minimize = 6;
    public const int KeyboardLowLevel = 13;
    public const int KeyDown = 0x0100;
    public const int KeyUp = 0x0101;
    public const int SystemKeyDown = 0x0104;
    public const int SystemKeyUp = 0x0105;
    public const uint Tab = 0x09;
    public const uint Escape = 0x1B;
    public const uint Shift = 0x10;
    public const uint Control = 0x11;
    public const uint Menu = 0x12;
    public const uint F4 = 0x73;
    public const uint LeftWindows = 0x5B;
    public const uint RightWindows = 0x5C;
    public static readonly IntPtr Topmost = new(-1);

    public delegate bool MonitorEnumerationProcedure(IntPtr monitor, IntPtr deviceContext, IntPtr rectangle, IntPtr data);
    public delegate IntPtr LowLevelKeyboardProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MonitorInfo
    {
        public int Size;
        public Rectangle Monitor;
        public Rectangle Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumerationProcedure callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowCommand(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();
}
