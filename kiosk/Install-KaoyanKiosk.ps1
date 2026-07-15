#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$PublishDirectory,

    [ValidatePattern('^[A-Za-z0-9._-]{1,20}$')]
    [string]$AccountName = 'KaoyanKiosk',

    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'KaoyanFocus')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')

if (-not $PublishDirectory) {
    $PublishDirectory = Join-Path $PSScriptRoot '..\src\KaoyanFocus\bin\Release\net9.0-windows\win-x64\publish'
}

$prerequisites = & (Join-Path $PSScriptRoot 'Test-KioskPrerequisites.ps1')
if (-not $prerequisites.Ready) {
    throw ($prerequisites.Reasons -join [Environment]::NewLine)
}

$sourceDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$sourceExecutable = Join-Path $sourceDirectory 'KaoyanFocus.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "Published KaoyanFocus.exe not found in: $sourceDirectory"
}

if (-not (Test-IsAdministrator)) {
    throw 'Run this script from Windows PowerShell as an administrator.'
}

$targetDescription = "install KaoyanFocus for kiosk use, create or validate local standard account '$AccountName', and apply Assigned Access"
if (-not $PSCmdlet.ShouldProcess($env:COMPUTERNAME, $targetDescription)) {
    return
}

$existingUser = Get-LocalUser -Name $AccountName -ErrorAction SilentlyContinue
if ($existingUser) {
    $administrators = Get-LocalGroup -SID 'S-1-5-32-544'
    $isAdministrator = Get-LocalGroupMember -Group $administrators.Name -ErrorAction SilentlyContinue |
        Where-Object { $_.SID.Value -eq $existingUser.SID.Value }
    if ($isAdministrator) {
        throw "Existing account '$AccountName' is an administrator. Assigned Access requires a standard user."
    }

    if (-not $existingUser.Enabled) {
        Enable-LocalUser -Name $AccountName
    }
}
else {
    Write-Host "Create the password for the dedicated standard account '$AccountName'."
    $password = Read-Host -AsSecureString 'Password'
    New-LocalUser -Name $AccountName -Password $password -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword -Description 'KaoyanFocus Assigned Access account' | Out-Null
}

New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $InstallDirectory -Recurse -Force
$installedExecutable = Join-Path $InstallDirectory 'KaoyanFocus.exe'

$shortcutDirectory = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $shortcutDirectory 'Kaoyan Focus.lnk'
New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $InstallDirectory
$shortcut.Description = 'KaoyanFocus self-discipline timer'
$shortcut.Save()

$deploymentDirectory = Join-Path $env:ProgramData 'KaoyanFocusKiosk'
New-Item -ItemType Directory -Path $deploymentDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AssignedAccessSystem.ps1') -Destination $deploymentDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Deployment.Common.ps1') -Destination $deploymentDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Remove-KaoyanKiosk.ps1') -Destination $deploymentDirectory -Force

$profileId = [Guid]::NewGuid()
$configurationPath = Join-Path $deploymentDirectory 'AssignedAccess.xml'
$configuration = New-KaoyanAssignedAccessConfiguration -ExecutablePath $installedExecutable -AccountName $AccountName -ProfileId $profileId

$configuration | Set-Content -LiteralPath $configurationPath -Encoding UTF8
[xml](Get-Content -LiteralPath $configurationPath -Raw -Encoding UTF8) | Out-Null

$workerPath = Join-Path $deploymentDirectory 'Invoke-AssignedAccessSystem.ps1'
Invoke-AssignedAccessWorker -Action Apply -WorkerPath $workerPath -WorkingDirectory $deploymentDirectory -ConfigurationPath $configurationPath | Out-Null

Write-Host ''
Write-Host 'Assigned Access was applied successfully.' -ForegroundColor Green
Write-Host "Sign out, then sign in as '$AccountName' to enter the focus environment."
Write-Host 'Use Ctrl+Alt+Del and sign out to leave the restricted account.'
Write-Host "Recovery script: $deploymentDirectory\Remove-KaoyanKiosk.ps1"
