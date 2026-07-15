# Windows Assigned Access 部署

该方案使用 Windows 的“受限用户体验（Assigned Access）”，为 `KaoyanKiosk` 标准账户只放行 KaoyanFocus：

- 登录专用账户后自动启动 KaoyanFocus；
- 隐藏任务栏，鼠标移到底部也不会显示；
- Assigned Access 自动生成 AppLocker 规则，阻止该账户运行《原神》和其他未允许的桌面程序；
- 管理员账户不受限制，可用于恢复系统。

## 系统要求

- Windows 11 22H2 或更高版本；
- Windows 11 Pro、Enterprise、Education 或 IoT Enterprise；
- UAC 已启用；
- 使用本机控制台登录，不能通过远程桌面使用 kiosk。

Windows 家庭版不支持 Assigned Access。本机当前检测为家庭中文版，因此必须先升级到 Windows 11 专业版或更高版本。不要尝试用注册表伪造系统版本。

先检查，不修改系统：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\kiosk\Test-KioskPrerequisites.ps1
```

## 安装

1. 发布应用：

   ```powershell
   dotnet publish .\src\KaoyanFocus\KaoyanFocus.csproj --configuration Release
   ```

2. 以管理员身份打开 **Windows PowerShell 5.1**，在仓库目录执行：

   ```powershell
   .\kiosk\Install-KaoyanKiosk.ps1
   ```

3. 为新建的 `KaoyanKiosk` 标准账户设置密码。
4. 注销当前账户，登录 `KaoyanKiosk`。策略会自动启动程序并永久隐藏该账户的任务栏。

安装脚本把发布目录复制到 `C:\Program Files\KaoyanFocus`，把恢复文件保存到 `C:\ProgramData\KaoyanFocusKiosk`。它不会启用自动登录，也不会把策略应用到管理员账户。

## 日常退出与恢复学习

- 正常学习：登录 `KaoyanKiosk`，设置任务并进入专注模式。
- 应急退出：先在程序中使用“应急解锁”，让次数和状态成功保存；再按 `Ctrl+Alt+Del` 注销，登录日常账户。
- 返回学习：重新登录 `KaoyanKiosk`，点击“返回学习”。
- 完成全部任务后：按 `Ctrl+Alt+Del` 注销，再登录日常账户。

`Ctrl+Alt+Del` 是 Windows 保留的安全出口，Assigned Access 不会阻止它。知道管理员账户密码的人仍然可以切换账户绕过自律限制。

## 卸载和故障恢复

在管理员账户中，以管理员身份打开 Windows PowerShell：

```powershell
C:\ProgramData\KaoyanFocusKiosk\Remove-KaoyanKiosk.ps1
```

默认只解除 Assigned Access，不删除账户、状态或程序。完整清理：

```powershell
C:\ProgramData\KaoyanFocusKiosk\Remove-KaoyanKiosk.ps1 -RemoveAccount -RemoveApplication
```

解除策略后重启 Windows。若图形界面异常，可从管理员账户运行同一恢复脚本；不要从受限账户删除策略文件。

## 技术边界

WPF 是经典 Win32 桌面应用，不能使用只面向 UWP/Microsoft Edge 的单应用 kiosk。本方案使用 Pro 版支持的受限用户体验，并仅在允许列表中加入 KaoyanFocus。Enterprise/Education 还可以改用 Shell Launcher，但这不是当前脚本的目标。

微软官方资料：

- [Assigned Access 概述](https://learn.microsoft.com/windows/configuration/assigned-access/)
- [创建 Assigned Access XML](https://learn.microsoft.com/windows/configuration/assigned-access/configuration-file)
- [配置受限用户体验](https://learn.microsoft.com/windows/configuration/assigned-access/configure-multi-app-kiosk)
- [Assigned Access 自动应用的策略与 AppLocker 规则](https://learn.microsoft.com/windows/configuration/assigned-access/policy-settings)
