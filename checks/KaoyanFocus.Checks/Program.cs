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

CheckTaskRowParsing();
CheckCompletedDashboardCannotStart();
CheckLockSessionTransitions();
CheckTaskSwitchSaveFailure();
CheckModalTimingPause();
CheckSingleInstanceOwnership();

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
CheckCrossDayLoadRollsState();

const string announcement = "<p>2027年全国硕士研究生招生初试时间为2026年12月19日至20日。</p>";
Equal(new DateOnly(2026, 12, 19), ExamDateProvider.TryParseDate(announcement, today), "official date");
Equal(today, ExamDateProvider.TryParseDate("初试时间：2026年7月12日", today), "exam day accepted");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("初试时间为2025年12月20日", today), "past date rejected");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("页面没有日期", today), "missing date");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("初试时间另见。报名为2026年10月1日", today), "unrelated date rejected");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("初试时间为2026年13月40日", today), "invalid date rejected");
await CheckFetchRequestBoundaryAsync(today);
Console.WriteLine("All checks passed.");

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

static void CheckCompletedDashboardCannotStart()
{
    var completed = AppState.NewDay(new DateOnly(2026, 7, 12));
    completed.Started = true;
    completed.Tasks.Add(new StudyTask
    {
        Name = "高数",
        TargetSeconds = 60,
        ElapsedSeconds = 60,
        Confirmed = true
    });

    Equal(false, FocusRules.CanStartFromDashboard(completed), "completed day cannot start again");
    Equal(DashboardPrimaryAction.Completed, FocusRules.GetDashboardPrimaryAction(completed), "completed dashboard action");

    completed.EmergencyMode = true;
    Equal(DashboardPrimaryAction.Resume, FocusRules.GetDashboardPrimaryAction(completed), "emergency resume takes priority");
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

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(response(request));
    }
}
