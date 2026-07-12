# 考研自律神器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个 Windows WPF 桌面程序，显示考研倒计时，以“计时达标 + 手动确认”约束当日任务，并提供每天两次可持久化的应急解锁机会。

**Architecture:** 单个 WPF 进程包含主页与全屏锁定窗口，代码后台直接协调少量状态，不引入 MVVM 框架。领域规则、JSON 持久化和官方日期抓取各自独立；一个控制台检查项目使用断言验证核心逻辑，不引入测试包。

**Tech Stack:** C# 13、.NET 9、WPF、`System.Text.Json`、`HttpClient`、PowerShell、Git。

## Global Constraints

- 仅支持 Windows，无需管理员权限，不安装服务、驱动或自定义登录组件。
- 锁定窗口阻止普通关闭和 Alt+F4，但不拦截 Ctrl+Alt+Delete、任务管理器、关机或管理员操作。
- 数据只保存到 `%LocalAppData%\KaoyanFocus\state.json`。
- 网络只访问教育部官方页面；每 24 小时最多检查一次，每次最多两次 HTTP 请求且不连续重试，失败时使用有效缓存或手动日期。
- 任务开始后不能新增、删除、改名或缩短。
- 任务必须同时满足累计时长达标和手动确认才算完成。
- 每个本地自然日恰有两次应急解锁机会；应急期间暂停计时，直到用户手动返回。
- 不加入账号、云同步、数据库、排行榜、历史统计、自动更新或第三方依赖。

## File Map

- `KaoyanFocus.slnx`：解决方案入口。
- `src/KaoyanFocus/KaoyanFocus.csproj`：WPF 可执行项目。
- `src/KaoyanFocus/App.xaml`、`App.xaml.cs`：启动、恢复和窗口切换。
- `src/KaoyanFocus/AppState.cs`：持久化模型。
- `src/KaoyanFocus/FocusRules.cs`：倒计时、任务完成、应急和跨日规则。
- `src/KaoyanFocus/StateStore.cs`：原子 JSON 保存与损坏恢复。
- `src/KaoyanFocus/ExamDateProvider.cs`：教育部公告请求和日期解析。
- `src/KaoyanFocus/MainWindow.xaml`、`MainWindow.xaml.cs`：主页和任务设置。
- `src/KaoyanFocus/LockWindow.xaml`、`LockWindow.xaml.cs`：全屏锁定、计时、确认和应急解锁。
- `checks/KaoyanFocus.Checks/KaoyanFocus.Checks.csproj`、`Program.cs`：无第三方测试框架的回归检查。

---

### Task 1: 建立项目和领域规则

**Files:**
- Create: `KaoyanFocus.slnx`
- Create: `src/KaoyanFocus/KaoyanFocus.csproj`
- Create: `src/KaoyanFocus/AppState.cs`
- Create: `src/KaoyanFocus/FocusRules.cs`
- Create: `checks/KaoyanFocus.Checks/KaoyanFocus.Checks.csproj`
- Create: `checks/KaoyanFocus.Checks/Program.cs`

**Interfaces:**
- Produces: `AppState`, `StudyTask`, and static `FocusRules` methods used by all later tasks.

- [ ] **Step 1: Scaffold the solution and projects**

Run:

```powershell
dotnet new sln -n KaoyanFocus --format slnx
dotnet new wpf -n KaoyanFocus -o src/KaoyanFocus --framework net9.0
dotnet new console -n KaoyanFocus.Checks -o checks/KaoyanFocus.Checks --framework net9.0
```

Replace `checks/KaoyanFocus.Checks/KaoyanFocus.Checks.csproj` so the console project can reference the Windows-targeted WPF assembly:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\KaoyanFocus\KaoyanFocus.csproj" />
  </ItemGroup>
</Project>
```

Then run:

```powershell
dotnet sln KaoyanFocus.slnx add src/KaoyanFocus/KaoyanFocus.csproj checks/KaoyanFocus.Checks/KaoyanFocus.Checks.csproj
```

Expected: both projects are added and no NuGet package is installed.

- [ ] **Step 2: Write the failing domain checks**

Replace `checks/KaoyanFocus.Checks/Program.cs` with:

```csharp
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
Console.WriteLine("All checks passed.");
```

- [ ] **Step 3: Run checks and verify failure**

Run: `dotnet run --project checks/KaoyanFocus.Checks`

Expected: build fails because `AppState`, `StudyTask`, and `FocusRules` do not exist.

- [ ] **Step 4: Add the minimal domain model**

Create `src/KaoyanFocus/AppState.cs`:

```csharp
namespace KaoyanFocus;

public sealed class AppState
{
    public DateOnly Day { get; set; }
    public DateOnly? ExamDate { get; set; }
    public string ExamDateSource { get; set; } = "none";
    public string? ExamDateUrl { get; set; }
    public DateTimeOffset? ExamDateUpdatedAt { get; set; }
    public DateTimeOffset? ExamDateCheckedAt { get; set; }
    public List<StudyTask> Tasks { get; set; } = [];
    public List<DailyRecord> Archive { get; set; } = [];
    public bool Started { get; set; }
    public bool EmergencyMode { get; set; }
    public int EmergencyUses { get; set; }
    public string? ActiveTaskId { get; set; }
    public bool RecoveryWarning { get; set; }

    public static AppState NewDay(DateOnly day) => new() { Day = day };
}

public sealed class StudyTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int TargetSeconds { get; set; }
    public int ElapsedSeconds { get; set; }
    public bool Confirmed { get; set; }
}

public sealed class DailyRecord
{
    public DateOnly Day { get; set; }
    public List<StudyTask> Tasks { get; set; } = [];
}
```

Create `src/KaoyanFocus/FocusRules.cs`:

```csharp
namespace KaoyanFocus;

public static class FocusRules
{
    public static int DaysUntil(DateOnly today, DateOnly exam) => exam.DayNumber - today.DayNumber;

    public static bool CanStart(AppState state) =>
        state.Tasks.Count > 0 && state.Tasks.All(t =>
            !string.IsNullOrWhiteSpace(t.Name) && t.TargetSeconds >= 60);

    public static bool AllTasksComplete(AppState state) =>
        state.Tasks.Count > 0 && state.Tasks.All(t =>
            t.ElapsedSeconds >= t.TargetSeconds && t.Confirmed);

    public static bool TryUseEmergency(AppState state)
    {
        if (state.EmergencyUses >= 2) return false;
        state.EmergencyUses++;
        state.EmergencyMode = true;
        state.ActiveTaskId = null;
        return true;
    }

    public static void RollTo(AppState state, DateOnly day)
    {
        if (state.Day == day) return;
        if (state.Tasks.Count > 0)
        {
            state.Archive.Add(new DailyRecord
            {
                Day = state.Day,
                Tasks = state.Tasks.Select(t => new StudyTask
                {
                    Id = t.Id,
                    Name = t.Name,
                    TargetSeconds = t.TargetSeconds,
                    ElapsedSeconds = t.ElapsedSeconds,
                    Confirmed = t.Confirmed
                }).ToList()
            });
        }
        state.Day = day;
        state.Tasks.Clear();
        state.Started = false;
        state.EmergencyMode = false;
        state.EmergencyUses = 0;
        state.ActiveTaskId = null;
    }
}
```

- [ ] **Step 5: Run checks and build**

Run:

```powershell
dotnet run --project checks/KaoyanFocus.Checks
dotnet build KaoyanFocus.slnx --no-restore
```

Expected: `All checks passed.` and `Build succeeded`.

- [ ] **Step 6: Commit**

```powershell
git add KaoyanFocus.slnx src checks
git commit -m "feat: add focus domain rules"
```

---

### Task 2: 持久化状态并安全恢复

**Files:**
- Create: `src/KaoyanFocus/StateStore.cs`
- Modify: `checks/KaoyanFocus.Checks/Program.cs`

**Interfaces:**
- Consumes: `AppState.NewDay(DateOnly)`.
- Produces: `StateStore.DefaultPath`, `LoadOrCreate(DateOnly)`, and `Save(AppState)`.

- [ ] **Step 1: Append failing persistence checks**

Append before the final `Console.WriteLine` in `checks/KaoyanFocus.Checks/Program.cs`:

```csharp
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
```

- [ ] **Step 2: Verify failure**

Run: `dotnet run --project checks/KaoyanFocus.Checks`

Expected: build fails because `StateStore` does not exist.

- [ ] **Step 3: Implement atomic JSON storage**

Create `src/KaoyanFocus/StateStore.cs`:

```csharp
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
```

- [ ] **Step 4: Run regression checks**

Run: `dotnet run --project checks/KaoyanFocus.Checks`

Expected: `All checks passed.`

- [ ] **Step 5: Commit**

```powershell
git add src/KaoyanFocus/StateStore.cs checks/KaoyanFocus.Checks/Program.cs
git commit -m "feat: persist focus state safely"
```

---

### Task 3: 获取并解析官方考试日期

**Files:**
- Create: `src/KaoyanFocus/ExamDateProvider.cs`
- Modify: `checks/KaoyanFocus.Checks/Program.cs`

**Interfaces:**
- Produces: `ExamDateResult(DateOnly Date, string Url)`, `ExamDateProvider.TryParseDate(string, DateOnly)`, and `FetchAsync(DateOnly, CancellationToken)`.

- [ ] **Step 1: Append failing parser checks**

Append before the final output in `checks/KaoyanFocus.Checks/Program.cs`:

```csharp
const string announcement = "<p>2027年全国硕士研究生招生初试时间为2026年12月19日至20日。</p>";
Equal(new DateOnly(2026, 12, 19), ExamDateProvider.TryParseDate(announcement, today), "official date");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("初试时间为2025年12月20日", today), "past date rejected");
Equal<DateOnly?>(null, ExamDateProvider.TryParseDate("页面没有日期", today), "missing date");
```

- [ ] **Step 2: Verify failure**

Run: `dotnet run --project checks/KaoyanFocus.Checks`

Expected: build fails because `ExamDateProvider` does not exist.

- [ ] **Step 3: Implement the official-page provider**

Create `src/KaoyanFocus/ExamDateProvider.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;

namespace KaoyanFocus;

public sealed record ExamDateResult(DateOnly Date, string Url);

public sealed partial class ExamDateProvider(HttpClient http)
{
    const string ListingUrl = "https://www.moe.gov.cn/jyb_xwfb/gzdt_gzdt/s5987/";

    [GeneratedRegex("href=[\"'](?<url>[^\"']+)[\"'][^>]*>.*?全国硕士研究生考试招生工作", RegexOptions.Singleline)]
    private static partial Regex AnnouncementLink();

    [GeneratedRegex("初试时间.{0,12}?(?<year>20\\d{2})年(?<month>\\d{1,2})月(?<day>\\d{1,2})日", RegexOptions.Singleline)]
    private static partial Regex ExamDateText();

    public static DateOnly? TryParseDate(string html, DateOnly today)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
        var match = ExamDateText().Match(text);
        if (!match.Success) return null;
        var date = new DateOnly(
            int.Parse(match.Groups["year"].Value),
            int.Parse(match.Groups["month"].Value),
            int.Parse(match.Groups["day"].Value));
        return date >= today ? date : null;
    }

    public async Task<ExamDateResult?> FetchAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var listing = await http.GetStringAsync(ListingUrl, cancellationToken);
        var match = AnnouncementLink().Match(listing);
        if (!match.Success) return null;
        var url = new Uri(new Uri(ListingUrl), match.Groups["url"].Value).ToString();
        var html = await http.GetStringAsync(url, cancellationToken);
        var date = TryParseDate(html, today);
        return date is null ? null : new(date.Value, url);
    }
}
```

- [ ] **Step 4: Run parser checks and build**

Run:

```powershell
dotnet run --project checks/KaoyanFocus.Checks
dotnet build KaoyanFocus.slnx --no-restore
```

Expected: checks pass and build succeeds. Do not make the automated check depend on the live website.

- [ ] **Step 5: Commit**

```powershell
git add src/KaoyanFocus/ExamDateProvider.cs checks/KaoyanFocus.Checks/Program.cs
git commit -m "feat: fetch official exam date"
```

---

### Task 4: 构建主页和任务设置

**Files:**
- Modify: `src/KaoyanFocus/App.xaml`
- Modify: `src/KaoyanFocus/App.xaml.cs`
- Modify: `src/KaoyanFocus/MainWindow.xaml`
- Modify: `src/KaoyanFocus/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `StateStore`, `ExamDateProvider`, `FocusRules.CanStart`, and `AppState`.
- Produces: main-window callbacks `StartRequested` and `ResumeRequested` for app-level navigation.

- [ ] **Step 1: Make startup explicit**

Replace `src/KaoyanFocus/App.xaml` with:

```xml
<Application x:Class="KaoyanFocus.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Application.Resources>
    <SolidColorBrush x:Key="Accent" Color="#2855D9"/>
  </Application.Resources>
</Application>
```

- [ ] **Step 2: Add the card-based main window**

Replace `src/KaoyanFocus/MainWindow.xaml` with:

```xml
<Window x:Class="KaoyanFocus.MainWindow"
 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
 Title="考研自律神器" Width="760" Height="680" MinWidth="620" MinHeight="560"
 Background="#EEF4FF" WindowStartupLocation="CenterScreen">
  <ScrollViewer>
    <Border Margin="32" Padding="28" Background="White" CornerRadius="18">
      <StackPanel>
        <TextBlock Text="距离考研还有" Foreground="#667085"/>
        <TextBlock x:Name="DaysText" FontSize="52" FontWeight="Bold" Foreground="{StaticResource Accent}"/>
        <TextBlock x:Name="DateSourceText" Foreground="#667085" Margin="0,0,0,22"/>
        <TextBlock Text="今日任务" FontSize="22" FontWeight="SemiBold"/>
        <ItemsControl x:Name="TaskList" Margin="0,12,0,8">
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <Grid Margin="0,0,0,8">
                <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="100"/><ColumnDefinition Width="48"/></Grid.ColumnDefinitions>
                <TextBox Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,8,0" Padding="10"/>
                <TextBox Grid.Column="1" Text="{Binding TargetMinutes, UpdateSourceTrigger=PropertyChanged}" Padding="10"/>
                <Button Grid.Column="2" Content="删" Tag="{Binding}" Click="DeleteTask_Click" Margin="8,0,0,0"/>
              </Grid>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <Button Content="＋ 添加任务" Click="AddTask_Click" Padding="10" HorizontalAlignment="Left"/>
        <TextBlock x:Name="EmergencyText" Margin="0,18,0,10" Foreground="#667085"/>
        <TextBlock x:Name="ErrorText" Foreground="#C62828" Margin="0,0,0,8" TextWrapping="Wrap"/>
        <Button x:Name="PrimaryButton" Content="开始今日学习" Click="PrimaryButton_Click"
                Padding="16" Background="{StaticResource Accent}" Foreground="White" FontWeight="SemiBold"/>
        <StackPanel x:Name="ManualDatePanel" Visibility="Collapsed" Margin="0,16,0,0">
          <TextBlock Text="暂未获取官方日期，请设置备用日期"/>
          <DatePicker x:Name="ManualDatePicker" Margin="0,8,0,0" SelectedDateChanged="ManualDateChanged"/>
        </StackPanel>
      </StackPanel>
    </Border>
  </ScrollViewer>
</Window>
```

- [ ] **Step 3: Implement main-window behavior without a view-model framework**

Create a small UI-only row type and bind it in `MainWindow.xaml.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace KaoyanFocus;

public partial class MainWindow : Window
{
    readonly AppState state;
    readonly StateStore store;
    readonly ObservableCollection<TaskRow> rows = [];
    public event Action? StartRequested;
    public event Action? ResumeRequested;

    public MainWindow(AppState state, StateStore store)
    {
        InitializeComponent();
        this.state = state;
        this.store = store;
        foreach (var task in state.Tasks) rows.Add(new(task));
        TaskList.ItemsSource = rows;
        RefreshView();
    }

    public void RefreshView()
    {
        DaysText.Text = state.ExamDate is { } date
            ? $"{FocusRules.DaysUntil(DateOnly.FromDateTime(DateTime.Today), date)} 天"
            : "待设置";
        DateSourceText.Text = state.ExamDateSource switch
        {
            "official" => "日期来源：教育部官方公告",
            "manual" => "日期来源：手动备用",
            _ => "正在获取官方考试日期"
        };
        EmergencyText.Text = $"今日剩余应急解锁：{2 - state.EmergencyUses} 次";
        ManualDatePanel.Visibility = state.ExamDate is null ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Content = state.EmergencyMode ? "返回学习" : "开始今日学习";
        TaskList.IsEnabled = !state.Started;
        if (state.RecoveryWarning)
            ErrorText.Text = "检测到损坏的数据文件，原文件已备份；请重新设置今天的任务。";
    }

    void AddTask_Click(object sender, RoutedEventArgs e) => rows.Add(new(new StudyTask()));
    void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TaskRow row }) rows.Remove(row);
    }

    void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (state.EmergencyMode) { ResumeRequested?.Invoke(); return; }
        if (state.ExamDate is null) { ErrorText.Text = "请先等待官方日期获取，或设置备用考试日期。"; return; }
        state.Tasks = rows.Select(r => r.ToTask()).ToList();
        if (!FocusRules.CanStart(state)) { ErrorText.Text = "请至少添加一项名称非空、时长不少于 1 分钟的任务。"; return; }
        state.Started = true;
        store.Save(state);
        StartRequested?.Invoke();
    }

    void ManualDateChanged(object sender, RoutedEventArgs e)
    {
        if (ManualDatePicker.SelectedDate is not { } value) return;
        var date = DateOnly.FromDateTime(value);
        if (date < DateOnly.FromDateTime(DateTime.Today)) { ErrorText.Text = "考试日期不能早于今天。"; return; }
        state.ExamDate = date;
        state.ExamDateSource = "manual";
        store.Save(state);
        RefreshView();
    }
}

public sealed class TaskRow(StudyTask task)
{
    public string Name { get; set; } = task.Name;
    public int TargetMinutes { get; set; } = Math.Max(1, task.TargetSeconds / 60);
    public StudyTask ToTask() => new() { Id = task.Id, Name = Name.Trim(), TargetSeconds = TargetMinutes * 60 };
}
```

- [ ] **Step 4: Wire loading and non-blocking date refresh in `App.xaml.cs`**

Use one shared state and store. The date request has a 5-second timeout and failures only reveal the manual picker:

```csharp
using System.Net.Http;
using System.Windows;

namespace KaoyanFocus;

public partial class App : Application
{
    readonly StateStore store = new(StateStore.DefaultPath);
    AppState state = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var today = DateOnly.FromDateTime(DateTime.Today);
        state = store.LoadOrCreate(today);
        if (state.Started && !state.EmergencyMode && !FocusRules.AllTasksComplete(state)) { ShowLock(); return; }
        ShowMain();
        if (state.ExamDateCheckedAt is null || DateTimeOffset.Now - state.ExamDateCheckedAt >= TimeSpan.FromHours(24))
            await RefreshExamDate(today);
    }

    void ShowMain()
    {
        var window = new MainWindow(state, store);
        window.StartRequested += () => { ShowLock(); window.Close(); };
        window.ResumeRequested += () => { state.EmergencyMode = false; store.Save(state); ShowLock(); window.Close(); };
        MainWindow = window;
        window.Show();
    }

    void ShowLock()
    {
        var window = new LockWindow(state, store);
        window.Unlocked += ShowMain;
        MainWindow = window;
        window.Show();
    }

    async Task RefreshExamDate(DateOnly today)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KaoyanFocus/1.0");
            var result = await new ExamDateProvider(http).FetchAsync(today, CancellationToken.None);
            if (result is not null)
            {
                state.ExamDate = result.Date;
                state.ExamDateSource = "official";
                state.ExamDateUrl = result.Url;
                state.ExamDateUpdatedAt = DateTimeOffset.Now;
            }
            state.ExamDateCheckedAt = DateTimeOffset.Now;
            store.Save(state);
            if (MainWindow is KaoyanFocus.MainWindow main) main.RefreshView();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            state.ExamDateCheckedAt = DateTimeOffset.Now;
            store.Save(state);
        }
    }
}
```

`LockWindow` is completed in Task 5. For this dashboard checkpoint, create this compiling shell as `LockWindow.xaml`:

```xml
<Window x:Class="KaoyanFocus.LockWindow"
 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
 Title="专注模式" Width="520" Height="320">
  <TextBlock Text="锁定界面将在下一任务完成" HorizontalAlignment="Center" VerticalAlignment="Center"/>
</Window>
```

Create `LockWindow.xaml.cs`:

```csharp
using System.Windows;

namespace KaoyanFocus;

public partial class LockWindow : Window
{
    public event Action? Unlocked;
    public LockWindow(AppState state, StateStore store) => InitializeComponent();
}
```

- [ ] **Step 5: Build and manually inspect the main window**

Run:

```powershell
dotnet build KaoyanFocus.slnx --no-restore
dotnet run --project src/KaoyanFocus
```

Expected: card-style home opens, tasks can be added/deleted, invalid tasks show an inline error, and a manual date is accepted only when not in the past.

- [ ] **Step 6: Commit**

```powershell
git add src/KaoyanFocus
git commit -m "feat: add task setup dashboard"
```

---

### Task 5: 实现全屏锁定、计时、确认和应急解锁

**Files:**
- Create or replace: `src/KaoyanFocus/LockWindow.xaml`
- Create or replace: `src/KaoyanFocus/LockWindow.xaml.cs`
- Modify: `checks/KaoyanFocus.Checks/Program.cs`

**Interfaces:**
- Consumes: `AppState`, `StudyTask`, `StateStore`, `FocusRules.TryUseEmergency`, `FocusRules.AllTasksComplete`, and `FocusRules.RollTo`.
- Produces: `LockWindow.Unlocked` event used by `App.ShowLock()`.

- [ ] **Step 1: Add a failing tick rule check**

Append before the final output in `checks/KaoyanFocus.Checks/Program.cs`:

```csharp
var timed = new StudyTask { TargetSeconds = 2 };
FocusRules.AddElapsed(timed, 1);
Equal(1, timed.ElapsedSeconds, "timer tick");
FocusRules.AddElapsed(timed, 5);
Equal(2, timed.ElapsedSeconds, "timer capped at target");
```

Run: `dotnet run --project checks/KaoyanFocus.Checks`

Expected: build fails because `FocusRules.AddElapsed` does not exist.

- [ ] **Step 2: Add the minimal elapsed-time rule**

Add to `FocusRules`:

```csharp
public static void AddElapsed(StudyTask task, int seconds)
{
    if (seconds > 0) task.ElapsedSeconds = Math.Min(task.TargetSeconds, task.ElapsedSeconds + seconds);
}
```

Run: `dotnet run --project checks/KaoyanFocus.Checks`

Expected: `All checks passed.`

- [ ] **Step 3: Build the lock-window layout**

Replace `src/KaoyanFocus/LockWindow.xaml` with a borderless maximized window containing:

```xml
<Window x:Class="KaoyanFocus.LockWindow"
 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
 WindowStyle="None" ResizeMode="NoResize" WindowState="Maximized" Topmost="True"
 Background="#EEF4FF" Closing="Window_Closing">
  <Grid Margin="60">
    <Grid.ColumnDefinitions><ColumnDefinition Width="2*"/><ColumnDefinition Width="3*"/></Grid.ColumnDefinitions>
    <StackPanel VerticalAlignment="Center">
      <TextBlock Text="距离考研还有" Foreground="#667085"/>
      <TextBlock x:Name="DaysText" FontSize="64" FontWeight="Bold" Foreground="#2855D9"/>
      <TextBlock x:Name="CurrentTaskText" FontSize="28" FontWeight="SemiBold" Margin="0,32,0,8"/>
      <TextBlock x:Name="TimerText" FontSize="54" FontFamily="Consolas"/>
      <Button x:Name="ConfirmButton" Content="确认完成本任务" Click="Confirm_Click" Margin="0,20,0,0" Padding="14" IsEnabled="False"/>
      <Button x:Name="EmergencyButton" Click="Emergency_Click" Margin="0,48,0,0" Padding="12" Background="#FFF4E5"/>
    </StackPanel>
    <ItemsControl x:Name="TaskList" Grid.Column="1" Margin="48,0,0,0">
      <ItemsControl.ItemTemplate>
        <DataTemplate>
          <Button Tag="{Binding}" Click="Task_Click" Margin="0,0,0,10" Padding="18" HorizontalContentAlignment="Stretch">
            <Grid><TextBlock Text="{Binding Name}"/><TextBlock Text="{Binding ProgressText}" HorizontalAlignment="Right"/></Grid>
          </Button>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
  </Grid>
</Window>
```

- [ ] **Step 4: Implement monotonic timing, sleep handling, rollover, and safe exits**

Replace `LockWindow.xaml.cs` with:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KaoyanFocus;

public partial class LockWindow : Window
{
    readonly AppState state;
    readonly StateStore store;
    readonly Stopwatch stopwatch = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool allowClose;
    int savedWholeSeconds;
    int ticks;
    public event Action? Unlocked;

    public LockWindow(AppState state, StateStore store)
    {
        InitializeComponent();
        this.state = state;
        this.store = store;
        state.ActiveTaskId = null;
        TaskList.ItemsSource = state.Tasks.Select(t => new LockTaskRow(t)).ToList();
        timer.Tick += Timer_Tick;
        timer.Start();
        SystemEvents.PowerModeChanged += PowerModeChanged;
        Closed += (_, _) => SystemEvents.PowerModeChanged -= PowerModeChanged;
        RefreshView();
    }

    void Task_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LockTaskRow row } || row.Task.Confirmed) return;
        FlushElapsed();
        state.ActiveTaskId = row.Task.Id;
        stopwatch.Restart();
        savedWholeSeconds = 0;
        store.Save(state);
        RefreshView();
    }

    void Timer_Tick(object? sender, EventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today != state.Day)
        {
            FlushElapsed();
            FocusRules.RollTo(state, today);
            ExitToMain();
            return;
        }
        FlushElapsed();
        if (++ticks % 5 == 0) store.Save(state);
        RefreshView();
    }

    void FlushElapsed()
    {
        if (state.ActiveTaskId is null) return;
        var task = state.Tasks.Single(t => t.Id == state.ActiveTaskId);
        var whole = (int)stopwatch.Elapsed.TotalSeconds;
        FocusRules.AddElapsed(task, whole - savedWholeSeconds);
        savedWholeSeconds = whole;
    }

    void Confirm_Click(object sender, RoutedEventArgs e)
    {
        FlushElapsed();
        var task = ActiveTask();
        if (task is null || task.ElapsedSeconds < task.TargetSeconds) return;
        task.Confirmed = true;
        stopwatch.Reset();
        state.ActiveTaskId = null;
        if (FocusRules.AllTasksComplete(state))
        {
            state.Started = false;
            ExitToMain();
            return;
        }
        store.Save(state);
        RefreshView();
    }

    void Emergency_Click(object sender, RoutedEventArgs e)
    {
        var remaining = 2 - state.EmergencyUses;
        if (remaining <= 0) return;
        var answer = MessageBox.Show(
            $"本次使用后，今天还剩 {remaining - 1} 次应急解锁。确认使用吗？",
            "应急解锁", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes && FocusRules.TryUseEmergency(state)) ExitToMain();
    }

    void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Suspend) return;
        FlushElapsed();
        stopwatch.Reset();
        state.ActiveTaskId = null;
        store.Save(state);
        Dispatcher.Invoke(RefreshView);
    }

    StudyTask? ActiveTask() => state.ActiveTaskId is null
        ? null : state.Tasks.Single(t => t.Id == state.ActiveTaskId);

    void RefreshView()
    {
        var active = ActiveTask();
        DaysText.Text = state.ExamDate is { } exam
            ? $"{FocusRules.DaysUntil(DateOnly.FromDateTime(DateTime.Today), exam)} 天"
            : "待设置";
        CurrentTaskText.Text = active?.Name ?? "请选择一项任务";
        var remaining = active is null ? 0 : Math.Max(0, active.TargetSeconds - active.ElapsedSeconds);
        TimerText.Text = TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm\:ss");
        ConfirmButton.IsEnabled = active is not null && active.ElapsedSeconds >= active.TargetSeconds;
        EmergencyButton.Content = $"应急解锁（剩余 {2 - state.EmergencyUses} 次）";
        EmergencyButton.IsEnabled = state.EmergencyUses < 2;
        TaskList.Items.Refresh();
    }

    void ExitToMain()
    {
        FlushElapsed();
        stopwatch.Stop();
        timer.Stop();
        store.Save(state);
        allowClose = true;
        Unlocked?.Invoke();
        Close();
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        FlushElapsed();
        store.Save(state);
        if (!allowClose) e.Cancel = true;
    }
}

public sealed class LockTaskRow(StudyTask task)
{
    public StudyTask Task { get; } = task;
    public string Name => (Task.Confirmed ? "✓ " : "") + Task.Name;
    public string ProgressText => $"{Task.ElapsedSeconds / 60} / {Task.TargetSeconds / 60} 分钟";
}
```

- [ ] **Step 6: Run checks and perform the lock smoke test**

Run:

```powershell
dotnet run --project checks/KaoyanFocus.Checks
dotnet run --project src/KaoyanFocus
```

Expected: Alt+F4 does not close the lock; only one task times at once; switching preserves elapsed seconds; early confirmation is disabled; two emergencies work and the third is disabled; all confirmed tasks return to the main window.

- [ ] **Step 7: Commit**

```powershell
git add src/KaoyanFocus checks/KaoyanFocus.Checks/Program.cs
git commit -m "feat: enforce daily focus session"
```

---

### Task 6: 完成恢复验证和可分发构建

**Files:**
- Modify: `src/KaoyanFocus/App.xaml.cs`
- Modify: `src/KaoyanFocus/KaoyanFocus.csproj`
- Create: `README.md`

**Interfaces:**
- Consumes all prior components; produces the first complete Windows executable.

- [ ] **Step 1: Add single-instance protection**

In `App.xaml.cs`, hold a named mutex for the process lifetime:

```csharp
Mutex? instanceMutex;
bool ownsInstanceMutex;

protected override async void OnStartup(StartupEventArgs e)
{
    instanceMutex = new Mutex(true, "KaoyanFocus.SingleInstance", out ownsInstanceMutex);
    if (!ownsInstanceMutex) { MessageBox.Show("考研自律神器已经在运行。"); Shutdown(); return; }
    // Continue with the Task 4 startup body.
}

protected override void OnExit(ExitEventArgs e)
{
    if (ownsInstanceMutex) instanceMutex?.ReleaseMutex();
    instanceMutex?.Dispose();
    base.OnExit(e);
}
```

- [ ] **Step 2: Configure a self-contained Windows build**

Keep `KaoyanFocus.csproj` dependency-free and add:

```xml
<PropertyGroup>
  <TargetFramework>net9.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <PublishSingleFile>true</PublishSingleFile>
  <SelfContained>true</SelfContained>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>
```

- [ ] **Step 3: Document use and safety boundary**

Create `README.md` containing launch/build commands, the two-emergency rule, the state-file path, and this explicit statement: “本程序是个人自律工具，不是 Windows 安全锁；任务管理器、Ctrl+Alt+Delete、关机及管理员操作可能绕过限制。”

- [ ] **Step 4: Run the complete verification**

Run:

```powershell
dotnet run --project checks/KaoyanFocus.Checks
dotnet build KaoyanFocus.slnx --configuration Release --no-restore
dotnet restore src/KaoyanFocus/KaoyanFocus.csproj --runtime win-x64
dotnet publish src/KaoyanFocus/KaoyanFocus.csproj --configuration Release --no-restore
git diff --check
```

Expected: checks print `All checks passed.`, build and publish report no errors, and the executable exists under `src/KaoyanFocus/bin/Release/net9.0-windows/win-x64/publish/`.

- [ ] **Step 5: Perform the end-to-end recovery test**

1. Start with a clean state file and set two one-minute tasks.
2. Start focus, time both tasks briefly, switch tasks, and confirm progress remains separate.
3. Use one emergency unlock; verify timing stops until “返回学习”.
4. Close the process from Task Manager, relaunch, and verify the unfinished lock restores without adding offline time.
5. Finish both timers, confirm both tasks, and verify normal unlock.
6. Replace `state.json` with invalid text, relaunch, and verify the recovery page/main page appears instead of a lock; verify a `state.corrupt-*.json` backup exists.
7. Restore the system date if it was changed for cross-day testing.

- [ ] **Step 6: Commit**

```powershell
git add src/KaoyanFocus README.md
git commit -m "feat: ship kaoyan focus app"
```
