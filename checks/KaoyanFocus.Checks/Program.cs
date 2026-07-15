using KaoyanFocus;

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{name}: expected {expected}, got {actual}");
}

var today = new DateOnly(2026, 7, 12);
Equal(160, FocusRules.DaysUntil(today, new DateOnly(2026, 12, 19)), "countdown");
Equal(0, FocusRules.DaysUntil(today, today), "exam day");
var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.FromHours(8));
Equal(true, FocusRules.ShouldRefreshExamDate(null, now), "date never checked");
Equal(false, FocusRules.ShouldRefreshExamDate(now.AddHours(-23), now), "date checked within 24 hours");
Equal(true, FocusRules.ShouldRefreshExamDate(now.AddHours(-24), now), "date checked 24 hours ago");
Equal(true, FocusRules.ShouldRefreshExamDate(today.AddDays(-1), now.AddMinutes(-1), today, now),
    "expired date bypasses recent-check cache");
CheckExpiredExamDateFallback();

CheckTaskRowParsing();
CheckCompletedDashboardStartsNewRound();
CheckDashboardCrossDayTransitions();
CheckLockSessionTransitions();
CheckTaskSwitchSaveFailure();
CheckModalTimingPause();
CheckSingleInstanceOwnership();
CheckInvalidStateFilesAreRecovered();
CheckProtectionSettings();
CheckIdlePolicy();
CheckWatchdogCommand();
CheckSystemShortcutPolicy();
await CheckWatchdogNormalStopAsync();

Equal("24:00:00", FocusRules.FormatDuration(24 * 60 * 60), "24-hour duration does not wrap");
Equal("25:01:01", FocusRules.FormatDuration(25 * 60 * 60 + 61), "duration uses total hours");

var state = AppState.NewDay(today);
state.Tasks.Add(new StudyTask { Name = "高数", TargetSeconds = 60, ElapsedSeconds = 60 });
Equal(false, FocusRules.AllTasksComplete(state), "confirmation required");
state.Tasks[0].Confirmed = true;
Equal(true, FocusRules.AllTasksComplete(state), "time and confirmation");
Equal(true, FocusRules.TryUseEmergency(state), "emergency one");
Equal(true, FocusRules.TryUseEmergency(state), "emergency two");
Equal(false, FocusRules.TryUseEmergency(state), "emergency limit");

var timed = new StudyTask { TargetSeconds = 2 };
FocusRules.AddElapsed(timed, 1);
Equal(1, timed.ElapsedSeconds, "timer tick");
FocusRules.AddElapsed(timed, 5);
Equal(2, timed.ElapsedSeconds, "timer capped at target");
FocusRules.AddElapsed(timed, -1);
Equal(2, timed.ElapsedSeconds, "timer ignores non-positive elapsed time");

FocusRules.RollTo(state, today.AddDays(1));
Equal(0, state.EmergencyUses, "daily reset");
Equal(0, state.Tasks.Count, "old tasks archived from active day");
Equal(1, state.Archive.Count, "archive retained");

CheckStateStorePersistence(state);
CheckCrossDayLoadRollsState();

const string announcement = "<p>2027年全国硕士研究生招生初试时间为2026年12月19日至20日。</p>";
Equal(new DateOnly(2026, 12, 19), ExamDateProvider.TryParseDate(announcement, today), "official date");
Equal(today, ExamDateProvider.TryParseDate("初试时间：2026年7月12日", today), "exam day accepted");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("初试时间为2025年12月20日", today), "past date rejected");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("页面没有日期", today), "missing date");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("初试时间另见。报名为2026年10月1日", today), "unrelated date rejected");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("初试时间为2026年13月40日", today), "invalid date rejected");
await CheckFetchRequestBoundaryAsync(today);
if (args.Contains("--native-smoke", StringComparer.Ordinal)) CheckNativeSmoke();
Console.WriteLine("All checks passed.");

static void CheckNativeSmoke()
{
    var monitors = MonitorLayout.GetAll();
    Equal(true, monitors.Count > 0, "at least one display monitor enumerated");
    Equal(true, monitors.Any(monitor => monitor.IsPrimary), "primary display monitor found");
    Equal(true, monitors.All(monitor => monitor.Width > 0 && monitor.Height > 0),
        "display monitor bounds are positive");
    var idleMilliseconds = UserIdleTime.GetMilliseconds();
    using var keyboard = new KeyboardShield();
    Equal(true, keyboard.Start(), "low-level keyboard hook installed");
    Console.WriteLine($"Native smoke: {monitors.Count} monitor(s), idle {idleMilliseconds} ms");
}

static void CheckProtectionSettings()
{
    Equal(true, ProtectionSettings.TryCreate(
        true, "5", "YuanShen.exe, GenshinImpact; yuanshen, C:\\Games\\StarRail.exe，HYP",
        out var settings), "strict settings accepted");
    Equal(5, settings.IdleTimeoutMinutes, "idle timeout parsed");
    Equal("YuanShen,GenshinImpact,StarRail,HYP", string.Join(',', settings.BlockedProcesses),
        "process names normalized and deduplicated");
    Equal(true, ProtectionSettings.BlocksProcess(settings, "yuanshen.exe"),
        "blocked process matching ignores case and extension");
    Equal(false, ProtectionSettings.BlocksProcess(settings, "notepad"), "unlisted process allowed");
    settings.Enabled = false;
    Equal(false, ProtectionSettings.BlocksProcess(settings, "YuanShen"),
        "disabled strict mode does not block processes");
    Equal(false, ProtectionSettings.TryCreate(true, "0", "YuanShen", out _),
        "zero idle timeout rejected");
    Equal(false, ProtectionSettings.TryCreate(true, "121", "YuanShen", out _),
        "excessive idle timeout rejected");
}

static void CheckIdlePolicy()
{
    Equal(false, IdlePolicy.ShouldPause(299_999, 5), "idle threshold not reached");
    Equal(true, IdlePolicy.ShouldPause(300_000, 5), "idle threshold reached");
    Equal(false, IdlePolicy.ShouldPause(ulong.MaxValue, 0), "invalid idle threshold stays safe");
}

static void CheckWatchdogCommand()
{
    Equal(true, WatchdogCommand.TryParse(
        [WatchdogCommand.Switch, "42", "Local\\KaoyanFocus.Watchdog.abc"], out var command),
        "watchdog command accepted");
    Equal(42, command.ParentProcessId, "watchdog parent parsed");
    Equal(false, WatchdogCommand.TryParse([WatchdogCommand.Switch, "0", "bad"], out _),
        "watchdog invalid parent rejected");
    Equal(false, WatchdogCommand.TryParse(["--other", "42", "Local\\KaoyanFocus.Watchdog.abc"], out _),
        "watchdog unknown command rejected");
}

static async Task CheckWatchdogNormalStopAsync()
{
    var eventName = $"Local\\KaoyanFocus.Watchdog.check.{Guid.NewGuid():N}";
    using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
    var host = FocusWatchdogHost.RunAsync(new WatchdogCommand(Environment.ProcessId, eventName));
    await Task.Delay(50);
    stopEvent.Set();
    await host.WaitAsync(TimeSpan.FromSeconds(5));
    Equal(true, host.IsCompletedSuccessfully, "watchdog exits after intentional stop signal");
}

static void CheckSystemShortcutPolicy()
{
    Equal(true, SystemShortcutPolicy.ShouldBlock(0x5B, false, false, false), "left Windows key blocked");
    Equal(true, SystemShortcutPolicy.ShouldBlock(0x09, true, false, false), "Alt+Tab blocked");
    Equal(true, SystemShortcutPolicy.ShouldBlock(0x1B, false, true, false), "Ctrl+Esc blocked");
    Equal(true, SystemShortcutPolicy.ShouldBlock(0x1B, false, true, true), "Ctrl+Shift+Esc blocked");
    Equal(false, SystemShortcutPolicy.ShouldBlock(0x41, false, true, false), "Ctrl+A allowed");
}

static void CheckTaskRowParsing()
{
    var original = new StudyTask
    {
        Id = "task-1",
        Name = "高数",
        TargetSeconds = 120,
        ElapsedSeconds = 75,
        Confirmed = true
    };
    var valid = new TaskRow(original) { Name = "  线代  ", TargetMinutesText = "3" };

    Equal(true, valid.TryCreateTask(out var candidate), "integer minutes accepted");
    Equal("线代", candidate.Name, "task name trimmed");
    Equal(180, candidate.TargetSeconds, "minutes converted to seconds");
    Equal(75, candidate.ElapsedSeconds, "elapsed progress preserved");
    Equal(true, candidate.Confirmed, "confirmation preserved");

    foreach (var invalid in new[] { "", " ", "abc", "1.5", "0", "-1", "35791395", "999999999999999999999" })
    {
        var row = new TaskRow(original) { TargetMinutesText = invalid };
        Equal(false, row.TryCreateTask(out _), $"invalid minutes rejected: {invalid}");
    }

    var rows = new[]
    {
        new TaskRow(original) { TargetMinutesText = "4" },
        new TaskRow(new StudyTask { Name = "英语", TargetSeconds = 60 }) { TargetMinutesText = "bad" }
    };
    Equal(false, TaskSetup.TryBuildTasks(rows, out _), "invalid candidate list rejected atomically");
    Equal(120, original.TargetSeconds, "invalid candidate list leaves original task unchanged");
}

static void CheckExpiredExamDateFallback()
{
    var today = new DateOnly(2026, 7, 12);
    var expired = AppState.NewDay(today);
    expired.ExamDate = today.AddDays(-1);
    expired.ExamDateSource = "official";
    expired.ExamDateCheckedAt = new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.FromHours(8));

    var display = ExamDateDashboard.Describe(expired, today);
    Equal("待设置", display.DaysText, "expired exam date never shows negative days");
    Equal(true, display.ShowManualDate, "expired cache keeps manual fallback visible");
    Equal("官方日期已过期且刷新失败，请设置手动备用日期", display.SourceText,
        "expired recent failure is explicit");
    Equal(false, FocusRules.HasUsableExamDate(expired, today),
        "expired cache cannot satisfy the dashboard date requirement");
}

static void CheckCompletedDashboardStartsNewRound()
{
    var day = new DateOnly(2026, 7, 12);
    var checkedAt = new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.FromHours(8));
    var completed = AppState.NewDay(day);
    completed.EmergencyUses = 2;
    completed.ExamDate = new DateOnly(2026, 12, 19);
    completed.ExamDateSource = "official";
    completed.ExamDateCheckedAt = checkedAt;
    completed.ActiveTaskId = "done";
    completed.Archive.Add(new DailyRecord
    {
        Day = day,
        Tasks = [new StudyTask { Id = "first-round", Name = "英语", TargetSeconds = 60 }]
    });
    completed.Tasks.Add(new StudyTask
    {
        Id = "done",
        Name = "高数",
        TargetSeconds = 60,
        ElapsedSeconds = 60,
        Confirmed = true
    });

    Equal(DashboardPrimaryAction.NewRound, FocusRules.GetDashboardPrimaryAction(completed), "completed dashboard offers a new round");

    completed.EmergencyMode = true;
    Equal(DashboardPrimaryAction.Resume, FocusRules.GetDashboardPrimaryAction(completed), "emergency resume takes priority");
    completed.EmergencyMode = false;

    var saved = false;
    Equal(true, DashboardDayTransition.TryStartNewRound(completed, _ => saved = true),
        "completed round is archived before editing the next round");
    Equal(true, saved, "new round is persisted");
    Equal(2, completed.Archive.Count, "same-day rounds remain separate archive records");
    Equal("first-round", completed.Archive[0].Tasks[0].Id, "prior round remains in archive");
    Equal("done", completed.Archive[1].Tasks[0].Id, "completed round is copied into archive");
    Equal(0, completed.Tasks.Count, "new round begins with an empty task editor");
    Equal(false, completed.Started, "new round is not started before tasks are entered");
    Equal(false, completed.EmergencyMode, "normal completion is not left in emergency mode");
    Equal(2, completed.EmergencyUses, "new round preserves daily emergency uses");
    Equal<string?>(null, completed.ActiveTaskId, "new round clears the active task");
    Equal(new DateOnly(2026, 12, 19), completed.ExamDate, "new round preserves exam date");
    Equal(checkedAt, completed.ExamDateCheckedAt, "new round preserves exam date check time");

    completed.Started = true;
    completed.ActiveTaskId = "failed-round";
    completed.Tasks.Add(new StudyTask
    {
        Id = "failed-round",
        Name = "政治",
        TargetSeconds = 60,
        ElapsedSeconds = 60,
        Confirmed = true
    });
    Equal(false, DashboardDayTransition.TryStartNewRound(
            completed, _ => throw new IOException("denied")),
        "failed new-round save is rejected");
    Equal(2, completed.Archive.Count, "failed save restores prior archive");
    Equal(1, completed.Tasks.Count, "failed save restores completed tasks");
    Equal("failed-round", completed.Tasks[0].Id, "failed save restores completed task data");
    Equal(true, completed.Started, "failed save restores started state");
    Equal("failed-round", completed.ActiveTaskId, "failed save restores active task");
    Equal(2, completed.EmergencyUses, "failed save preserves daily emergency uses");
    Equal(DashboardPrimaryAction.NewRound, FocusRules.GetDashboardPrimaryAction(completed),
        "failed save still displays the completed round");
}

static void CheckDashboardCrossDayTransitions()
{
    var oldDay = new DateOnly(2026, 7, 12);
    var nextDay = oldDay.AddDays(1);
    var now = new DateTimeOffset(2026, 7, 13, 0, 1, 0, TimeSpan.FromHours(8));
    foreach (var emergencyMode in new[] { false, true })
    {
        var dashboard = AppState.NewDay(oldDay);
        dashboard.Started = true;
        dashboard.EmergencyMode = emergencyMode;
        dashboard.EmergencyUses = emergencyMode ? 1 : 0;
        dashboard.Tasks.Add(new StudyTask
        {
            Id = "done",
            Name = "高数",
            TargetSeconds = 60,
            ElapsedSeconds = 60,
            Confirmed = true
        });

        Equal(true, DashboardDayTransition.TryRoll(dashboard, nextDay, now, _ => { }, _ => { }),
            $"{(emergencyMode ? "emergency" : "completed")} dashboard rolls across day");
        Equal(nextDay, dashboard.Day, "dashboard advances local day");
        Equal(0, dashboard.Tasks.Count, "dashboard rebuild source is cleared");
        Equal(1, dashboard.Archive.Count, "dashboard archives prior tasks");
        Equal(0, dashboard.EmergencyUses, "dashboard resets emergency uses");
    }

    var failed = AppState.NewDay(oldDay);
    failed.Started = true;
    failed.EmergencyMode = true;
    failed.EmergencyUses = 2;
    failed.Tasks.Add(new StudyTask { Id = "active", Name = "英语", TargetSeconds = 60 });
    failed.ActiveTaskId = "active";

    var failedRefreshes = 0;
    Equal(false, DashboardDayTransition.TryRoll(
            failed, nextDay, now, _ => throw new IOException("denied"), _ => failedRefreshes++),
        "failed cross-day save is rejected");
    Equal(oldDay, failed.Day, "failed cross-day save restores day");
    Equal(1, failed.Tasks.Count, "failed cross-day save restores tasks");
    Equal(0, failed.Archive.Count, "failed cross-day save restores archive");
    Equal(true, failed.EmergencyMode, "failed cross-day save restores emergency mode");
    Equal(2, failed.EmergencyUses, "failed cross-day save restores emergency uses");
    Equal("active", failed.ActiveTaskId, "failed cross-day save restores active task");
    Equal(0, failedRefreshes, "failed cross-day save does not refresh exam date");

    var expired = AppState.NewDay(oldDay);
    expired.ExamDate = oldDay;
    expired.ExamDateCheckedAt = now.AddMinutes(-1);
    var refreshes = 0;
    Equal(true, DashboardDayTransition.TryRoll(expired, nextDay, now, _ => { }, _ => refreshes++),
        "expired exam date rolls before refresh");
    Equal(1, refreshes, "expired exam date refreshes after successful cross-day save");
    Equal(false, DashboardDayTransition.TryRoll(expired, nextDay, now, _ => { }, _ => refreshes++),
        "same-day activation does not roll again");
    Equal(1, refreshes, "timer and activation cannot duplicate the cross-day refresh");
}

static void CheckLockSessionTransitions()
{
    var session = AppState.NewDay(new DateOnly(2026, 7, 12));
    session.Started = true;
    var first = new StudyTask { Id = "first", Name = "高数", TargetSeconds = 2 };
    var second = new StudyTask
    {
        Id = "second",
        Name = "英语",
        TargetSeconds = 2,
        ElapsedSeconds = 2,
        Confirmed = true
    };
    session.Tasks.AddRange([first, second]);

    Equal(true, FocusRules.TryActivateTask(session, first.Id), "unfinished task activated");
    Equal(first.Id, session.ActiveTaskId, "active task selected");
    Equal(false, FocusRules.TryActivateTask(session, second.Id), "confirmed task cannot activate");
    Equal(first.Id, session.ActiveTaskId, "rejected activation preserves active task");
    Equal(false, FocusRules.TryConfirmActiveTask(session), "early confirmation rejected");
    Equal(false, first.Confirmed, "early confirmation leaves task unfinished");

    FocusRules.AddElapsed(first, 2);
    Equal(true, FocusRules.TryConfirmActiveTask(session), "duration-qualified task confirmed");
    Equal(true, first.Confirmed, "manual confirmation recorded");
    Equal<string?>(null, session.ActiveTaskId, "confirmation pauses timer");
    Equal(false, session.Started, "all confirmed tasks finish session");

    session.ActiveTaskId = second.Id;
    FocusRules.PauseActiveTask(session);
    Equal<string?>(null, session.ActiveTaskId, "pause clears active task");
}

static void CheckTaskSwitchSaveFailure()
{
    var session = AppState.NewDay(new DateOnly(2026, 7, 12));
    session.Started = true;
    var oldTask = new StudyTask { Id = "old", Name = "高数", TargetSeconds = 100, ElapsedSeconds = 10 };
    var newTask = new StudyTask { Id = "new", Name = "英语", TargetSeconds = 100, ElapsedSeconds = 20 };
    session.Tasks.AddRange([oldTask, newTask]);
    session.ActiveTaskId = oldTask.Id;

    var timerRunning = true;
    var stopwatchRunning = true;
    var baseline = 7;
    string? nestedPersistedTaskId = null;
    var trace = new List<string>();

    var switched = LockTaskSwitch.TrySwitch(
        session,
        newTask.Id,
        flushElapsed: () =>
        {
            trace.Add("flush-old");
            FocusRules.AddElapsed(oldTask, 3);
        },
        stopTiming: () =>
        {
            trace.Add("stop");
            timerRunning = false;
            stopwatchRunning = false;
        },
        resetBaseline: () =>
        {
            trace.Add("reset");
            baseline = 0;
        },
        trySave: () =>
        {
            trace.Add("save");
            if (timerRunning || stopwatchRunning)
            {
                FocusRules.AddElapsed(newTask, 5); // models a nested Dispatcher tick
                nestedPersistedTaskId = session.ActiveTaskId;
            }
            return false;
        },
        restoreTiming: wasActive =>
        {
            trace.Add($"restore:{wasActive}");
            timerRunning = true;
            stopwatchRunning = wasActive;
        },
        startNewTiming: () => trace.Add("start-new"));

    Equal(false, switched, "failed save rejects task switch");
    Equal(oldTask.Id, session.ActiveTaskId, "failed save restores old active task");
    Equal(13, oldTask.ElapsedSeconds, "old task flushed before switch");
    Equal(20, newTask.ElapsedSeconds, "nested save loop cannot advance new task");
    Equal<string?>(null, nestedPersistedTaskId, "nested tick cannot persist intermediate task switch");
    Equal(0, baseline, "failed switch leaves a clean timing baseline");
    Equal(true, timerRunning, "failed switch restores dispatcher timer");
    Equal(true, stopwatchRunning, "failed switch restores active stopwatch");
    Equal("flush-old,stop,reset,save,restore:True", string.Join(',', trace), "switch failure ordering");

    trace.Clear();
    session.ActiveTaskId = oldTask.Id;
    timerRunning = true;
    stopwatchRunning = true;
    switched = LockTaskSwitch.TrySwitch(
        session,
        newTask.Id,
        () => trace.Add("flush-old"),
        () =>
        {
            trace.Add("stop");
            timerRunning = false;
            stopwatchRunning = false;
        },
        () => trace.Add("reset"),
        () =>
        {
            trace.Add($"save:{session.ActiveTaskId}:{timerRunning}:{stopwatchRunning}");
            return true;
        },
        wasActive => trace.Add($"restore:{wasActive}"),
        () =>
        {
            trace.Add("start-new");
            timerRunning = true;
            stopwatchRunning = true;
        });

    Equal(true, switched, "successful save commits task switch");
    Equal(newTask.Id, session.ActiveTaskId, "successful save keeps new task active");
    Equal(true, timerRunning, "successful switch starts dispatcher timer");
    Equal(true, stopwatchRunning, "successful switch starts new stopwatch");
    Equal(
        "flush-old,stop,reset,save:new:False:False,start-new",
        string.Join(',', trace),
        "new timing starts only after persisted switch");

    trace.Clear();
    session.ActiveTaskId = null;
    timerRunning = true;
    stopwatchRunning = false;
    switched = LockTaskSwitch.TrySwitch(
        session,
        oldTask.Id,
        () => trace.Add("flush"),
        () =>
        {
            timerRunning = false;
            stopwatchRunning = false;
        },
        () => { },
        () => false,
        wasActive =>
        {
            trace.Add($"restore:{wasActive}");
            timerRunning = true;
            stopwatchRunning = wasActive;
        },
        () => throw new Exception("new timing must not start after failed save"));

    Equal(false, switched, "inactive failed save rejects task switch");
    Equal<string?>(null, session.ActiveTaskId, "inactive failed save restores no active task");
    Equal(true, timerRunning, "inactive failure restores dispatcher timer");
    Equal(false, stopwatchRunning, "inactive failure leaves stopwatch stopped");
    Equal("flush,restore:False", string.Join(',', trace), "inactive timing state restored");
}

static void CheckModalTimingPause()
{
    var timerRunning = true;
    var stopwatchRunning = true;
    var nestedTicks = 0;
    var result = LockModalPause.Run(
        stopTiming: () =>
        {
            timerRunning = false;
            stopwatchRunning = false;
        },
        showModal: () =>
        {
            if (timerRunning || stopwatchRunning) nestedTicks++;
            return "answer";
        },
        restoreTiming: () =>
        {
            timerRunning = true;
            stopwatchRunning = true;
        });

    Equal("answer", result, "modal result returned");
    Equal(0, nestedTicks, "modal nested loop cannot tick while timing is paused");
    Equal(true, timerRunning, "dispatcher timer restored after modal");
    Equal(true, stopwatchRunning, "stopwatch restored after modal");
}

static void CheckSingleInstanceOwnership()
{
    var releaseCount = 0;

    SingleInstanceOwnership.ReleaseIfOwned(true, () => releaseCount++);
    Equal(1, releaseCount, "single-instance owner releases mutex");

    SingleInstanceOwnership.ReleaseIfOwned(false, () => releaseCount++);
    Equal(1, releaseCount, "single-instance non-owner does not release mutex");

    foreach (var expected in new Exception[]
             {
                 new UnauthorizedAccessException("denied"),
                 new WaitHandleCannotBeOpenedException("conflict")
             })
    {
        var result = SingleInstanceOwnership.Acquire(() => throw expected);
        Equal(false, result.Succeeded, $"expected mutex failure is handled: {expected.GetType().Name}");
        Equal<Mutex?>(null, result.Handle, $"failed mutex acquisition has no handle: {expected.GetType().Name}");
        Equal(false, result.OwnsMutex, $"failed mutex acquisition has no ownership: {expected.GetType().Name}");
        Equal(
            "无法创建单实例保护，可能存在同名系统对象或权限不足。程序将安全退出。",
            result.ErrorMessage,
            $"expected mutex failure has a clear message: {expected.GetType().Name}");
    }

    var unknownPropagated = false;
    try
    {
        SingleInstanceOwnership.Acquire(() => throw new InvalidOperationException("unknown"));
    }
    catch (InvalidOperationException)
    {
        unknownPropagated = true;
    }

    Equal(true, unknownPropagated, "unknown mutex failure is not swallowed");
}

static void CheckCrossDayLoadRollsState()
{
    var oldDay = new DateOnly(2026, 7, 12);
    var nextDay = oldDay.AddDays(1);
    var oldState = AppState.NewDay(oldDay);
    oldState.Tasks.Add(new StudyTask
    {
        Name = "英语",
        TargetSeconds = 1800,
        ElapsedSeconds = 600
    });
    FocusRules.TryUseEmergency(oldState);
    FocusRules.TryUseEmergency(oldState);

    var crossDayTemp = Path.Combine(
        Path.GetTempPath(), "KaoyanFocusChecks", Guid.NewGuid().ToString("N"));
    try
    {
        var crossDayStore = new StateStore(Path.Combine(crossDayTemp, "state.json"));
        crossDayStore.Save(oldState);
        var rolled = crossDayStore.LoadOrCreate(nextDay);

        Equal(nextDay, rolled.Day, "cross-day loaded day");
        Equal(0, rolled.Tasks.Count, "cross-day active tasks cleared");
        Equal(1, rolled.Archive.Count, "cross-day old tasks archived");
        Equal(oldDay, rolled.Archive[0].Day, "cross-day archive day");
        Equal(1, rolled.Archive[0].Tasks.Count, "cross-day archived task count");
        Equal("英语", rolled.Archive[0].Tasks[0].Name, "cross-day archived task");
        Equal(0, rolled.EmergencyUses, "cross-day emergency count reset");
    }
    finally
    {
        if (Directory.Exists(crossDayTemp)) Directory.Delete(crossDayTemp, true);
    }
}

static void CheckStateStorePersistence(AppState state)
{
    var temp = Path.Combine(Path.GetTempPath(), "KaoyanFocusChecks", Guid.NewGuid().ToString("N"));
    var originalDirectory = Environment.CurrentDirectory;
    try
    {
        Directory.CreateDirectory(temp);
        Environment.CurrentDirectory = temp;
        var store = new StateStore("state.json");
        state.StrictMode = new StrictModeSettings
        {
            Enabled = true,
            IdleTimeoutMinutes = 9,
            BlockedProcesses = ["YuanShen", "CustomGame"]
        };
        store.Save(state);
        var loaded = store.LoadOrCreate(state.Day);
        Equal(state.Day, loaded.Day, "relative state path saved day");
        Equal(state.EmergencyUses, loaded.EmergencyUses, "saved emergency count");
        Equal(true, loaded.StrictMode.Enabled, "strict mode enabled persisted");
        Equal(9, loaded.StrictMode.IdleTimeoutMinutes, "idle timeout persisted");
        Equal("YuanShen,CustomGame", string.Join(',', loaded.StrictMode.BlockedProcesses),
            "process blacklist persisted");

        for (var index = 0; index < 2; index++)
        {
            File.WriteAllText(Path.Combine(temp, "state.json"), "not-json");
            var recovered = store.LoadOrCreate(state.Day);
            Equal(true, recovered.RecoveryWarning, $"corrupt state warning {index}");
        }
        Equal(2, Directory.GetFiles(temp, "state.corrupt-*.json").Length,
            "rapid corrupt backups are unique and never overwritten");
    }
    finally
    {
        Environment.CurrentDirectory = originalDirectory;
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }
}

static void CheckInvalidStateFilesAreRecovered()
{
    var today = new DateOnly(2026, 7, 12);
    var temp = Path.Combine(Path.GetTempPath(), "KaoyanFocusChecks", Guid.NewGuid().ToString("N"));
    try
    {
        var invalidStates = new Dictionary<string, string>
        {
            ["null tasks"] = """{"Day":"2026-07-12","Tasks":null,"Archive":[]}""",
            ["null archive"] = """{"Day":"2026-07-12","Tasks":[],"Archive":null}""",
            ["null strict mode"] = """{"Day":"2026-07-12","Tasks":[],"Archive":[],"StrictMode":null}""",
            ["invalid idle timeout"] = """{"Day":"2026-07-12","Tasks":[],"Archive":[],"StrictMode":{"Enabled":true,"IdleTimeoutMinutes":0,"BlockedProcesses":[]}}""",
            ["null process blacklist"] = """{"Day":"2026-07-12","Tasks":[],"Archive":[],"StrictMode":{"Enabled":true,"IdleTimeoutMinutes":5,"BlockedProcesses":null}}""",
            ["empty id"] = """{"Day":"2026-07-12","Tasks":[{"Id":"","Name":"高数","TargetSeconds":60}],"Archive":[]}""",
            ["duplicate id"] = """{"Day":"2026-07-12","Tasks":[{"Id":"x","Name":"高数","TargetSeconds":60},{"Id":"x","Name":"英语","TargetSeconds":60}],"Archive":[]}""",
            ["empty name"] = """{"Day":"2026-07-12","Tasks":[{"Id":"x","Name":" ","TargetSeconds":60}],"Archive":[]}""",
            ["short target"] = """{"Day":"2026-07-12","Tasks":[{"Id":"x","Name":"高数","TargetSeconds":59}],"Archive":[]}""",
            ["negative elapsed"] = """{"Day":"2026-07-12","Tasks":[{"Id":"x","Name":"高数","TargetSeconds":60,"ElapsedSeconds":-1}],"Archive":[]}""",
            ["elapsed over target"] = """{"Day":"2026-07-12","Tasks":[{"Id":"x","Name":"高数","TargetSeconds":60,"ElapsedSeconds":61}],"Archive":[]}""",
            ["early confirmation"] = """{"Day":"2026-07-12","Tasks":[{"Id":"x","Name":"高数","TargetSeconds":60,"ElapsedSeconds":59,"Confirmed":true}],"Archive":[]}""",
            ["emergency underflow"] = """{"Day":"2026-07-12","Tasks":[],"Archive":[],"EmergencyUses":-1}""",
            ["emergency overflow"] = """{"Day":"2026-07-12","Tasks":[],"Archive":[],"EmergencyUses":3}""",
            ["started without tasks"] = """{"Day":"2026-07-12","Tasks":[],"Archive":[],"Started":true}""",
            ["missing active task"] = """{"Day":"2026-07-12","Tasks":[{"Id":"x","Name":"高数","TargetSeconds":60}],"Archive":[],"Started":true,"ActiveTaskId":"missing"}"""
        };

        foreach (var (name, json) in invalidStates)
        {
            var path = Path.Combine(temp, name.Replace(' ', '-') + ".json");
            Directory.CreateDirectory(temp);
            File.WriteAllText(path, json);
            var recovered = new StateStore(path).LoadOrCreate(today);
            Equal(true, recovered.RecoveryWarning, $"invalid state recovered: {name}");
            Equal(1, Directory.GetFiles(temp, Path.GetFileNameWithoutExtension(path) + ".corrupt-*.json").Length,
                $"invalid state backed up: {name}");
        }

        var crashPath = Path.Combine(temp, "legal-crash.json");
        var crash = AppState.NewDay(today);
        crash.Started = true;
        crash.Tasks.Add(new StudyTask { Id = "active", Name = "英语", TargetSeconds = 600, ElapsedSeconds = 120 });
        crash.ActiveTaskId = "active";
        var crashStore = new StateStore(crashPath);
        crashStore.Save(crash);
        var loaded = crashStore.LoadOrCreate(today);
        Equal(false, loaded.RecoveryWarning, "legal crash state remains recoverable");
        Equal("active", loaded.ActiveTaskId, "legal crash active task retained");

        var legacyPath = Path.Combine(temp, "legacy-without-strict-mode.json");
        File.WriteAllText(legacyPath, """{"Day":"2026-07-12","Tasks":[],"Archive":[]}""");
        var legacy = new StateStore(legacyPath).LoadOrCreate(today);
        Equal(true, legacy.StrictMode.Enabled, "legacy state receives strict-mode default");
        Equal(5, legacy.StrictMode.IdleTimeoutMinutes, "legacy state receives idle default");
    }
    finally
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }
}

static async Task CheckFetchRequestBoundaryAsync(DateOnly today)
{
    var officialHandler = new StubHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
    {
        "https://www.moe.gov.cn/jyb_xwfb/gzdt_gzdt/s5987/" =>
            "<a href='/jyb_xwfb/gzdt_gzdt/s5987/202610/t20261001_1.html'>全国硕士研究生考试招生工作</a>",
        "https://www.moe.gov.cn/jyb_xwfb/gzdt_gzdt/s5987/202610/t20261001_1.html" =>
            "<p>初试时间为2026年12月19日至20日</p>",
        _ => throw new Exception($"unexpected request: {request.RequestUri}")
    });
    var provider = new ExamDateProvider(officialHandler);

    var result = await provider.FetchAsync(today, CancellationToken.None);

    Equal(new DateOnly(2026, 12, 19), result?.Date, "fetched official date");
    Equal(2, officialHandler.RequestCount, "at most listing and first candidate requested");
    Equal("KaoyanFocus/1.0,KaoyanFocus/1.0", string.Join(',', officialHandler.UserAgents),
        "listing and announcement requests identify the app");

    var multipleAnchorHandler = new StubHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
    {
        "https://www.moe.gov.cn/jyb_xwfb/gzdt_gzdt/s5987/" =>
            "<a href='https://example.com/wrong'>其他公告</a>" +
            "<a href='/jyb_xwfb/gzdt_gzdt/s5987/202610/correct.html'>全国硕士研究生考试招生工作</a>",
        "https://www.moe.gov.cn/jyb_xwfb/gzdt_gzdt/s5987/202610/correct.html" =>
            "<p>初试时间：2026年12月19日</p>",
        _ => throw new Exception($"unexpected request: {request.RequestUri}")
    });
    provider = new ExamDateProvider(multipleAnchorHandler);

    result = await provider.FetchAsync(today, CancellationToken.None);

    Equal(new DateOnly(2026, 12, 19), result?.Date, "title associated with its own anchor");
    Equal(2, multipleAnchorHandler.RequestCount, "multiple-anchor candidate fetched once");

    var externalHandler = new StubHttpMessageHandler(_ =>
        "<a href='https://example.com/announcement'>全国硕士研究生考试招生工作</a>");
    provider = new ExamDateProvider(externalHandler);

    result = await provider.FetchAsync(today, CancellationToken.None);

    Equal<ExamDateResult?>(null, result, "external candidate rejected");
    Equal(1, externalHandler.RequestCount, "external candidate not requested");

    var redirectHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Found)
    {
        Headers = { Location = new Uri("https://example.com/redirected") }
    });
    provider = new ExamDateProvider(redirectHandler);

    result = await provider.FetchAsync(today, CancellationToken.None);

    Equal<ExamDateResult?>(null, result, "external redirect rejected");
    Equal(1, redirectHandler.RequestCount, "redirect does not add a request");
}

sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> response;

    public StubHttpMessageHandler(Func<HttpRequestMessage, string> response)
        : this(request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(response(request))
        })
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        this.response = response;

    public int RequestCount { get; private set; }
    public List<string> UserAgents { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        UserAgents.Add(request.Headers.UserAgent.ToString());
        return Task.FromResult(response(request));
    }
}
