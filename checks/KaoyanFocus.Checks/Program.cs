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
