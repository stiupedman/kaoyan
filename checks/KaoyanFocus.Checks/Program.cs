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
