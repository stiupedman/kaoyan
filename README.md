# 考研自律神器

一个仅供个人使用的 Windows 学习计时与自律工具。每天设置任务并开始专注后，需完成各任务计时并逐项确认，才能正常解锁。

## 运行、构建与发布

需要 .NET 9 SDK。源码运行：

```powershell
dotnet run --project src/KaoyanFocus/KaoyanFocus.csproj
```

Release 构建：

```powershell
dotnet restore KaoyanFocus.slnx
dotnet build KaoyanFocus.slnx --configuration Release --no-restore
```

发布自包含的 win-x64 单文件程序：

```powershell
dotnet restore src/KaoyanFocus/KaoyanFocus.csproj --runtime win-x64
dotnet publish src/KaoyanFocus/KaoyanFocus.csproj --configuration Release --no-restore
```

产物位于 `src/KaoyanFocus/bin/Release/net9.0-windows/win-x64/publish/`。

## 状态与规则

- 状态保存在 `%LOCALAPPDATA%\KaoyanFocus\state.json`。异常状态文件会备份为同目录下的 `state.corrupt-*.json`，随后进入恢复状态。
- 考试日期仅从教育部官方网站联网获取；成功或失败检查后 24 小时内不会重复联网检查。
- 每个自然日最多使用两次应急解锁。应急解锁后计时暂停，选择“返回学习”才会恢复专注锁定；跨日后次数重置。
- 程序使用单实例保护，重复启动时会提示已有实例正在运行。

## 家庭版强化模式

任务开始前可以配置“家庭版强化模式”，默认开启：

- 每个显示器都会被专注窗口覆盖；显示器连接状态变化后会自动重建覆盖窗口；
- 专注期间隐藏主任务栏和副屏任务栏，失去前台时持续重新置顶；
- 限制 Windows 键、`Alt+Tab`、`Alt+Esc`、`Alt+F4`、`Ctrl+Esc` 和 `Ctrl+Shift+Esc`；
- 轮询进程黑名单，发现《原神》等程序窗口时只将其最小化，不结束进程；
- 默认连续 5 分钟没有键鼠输入就暂停当前任务计时，恢复操作后继续；
- 独立看门狗监控专注进程；异常退出时先恢复任务栏，再重新启动程序并从持久化状态恢复专注页。

强化模式的离座时间和进程黑名单保存在状态文件中。应急解锁后可以调整或关闭强化模式，再选择“返回学习”。

`Ctrl+Alt+Del`、注销、关机和管理员操作始终保留为安全出口。若程序与看门狗都被结束，仍可绕过限制。

## 安全边界

本程序是个人自律工具，不是 Windows 安全锁；任务管理器、Ctrl+Alt+Delete、关机及管理员操作可能绕过限制。

此外，同时结束程序与看门狗、重启或注销、删除或修改状态文件、修改系统时间、从其他账户或操作系统启动，以及调试、替换或修改程序文件，都可能暂停、重置或绕过限制。程序不尝试阻止这些系统级或拥有本机权限的操作。

## Windows kiosk / Assigned Access

若要阻止从任务栏恢复《原神》等程序，可在 Windows 11 Pro、Enterprise、Education 或 IoT Enterprise 上使用仓库提供的 Assigned Access 部署方案。它为独立标准账户只放行 KaoyanFocus，并隐藏任务栏；家庭版不支持该功能。

部署、应急退出和恢复步骤见 [`kiosk/README.md`](kiosk/README.md)。
