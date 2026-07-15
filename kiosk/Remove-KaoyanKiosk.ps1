#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidatePattern('^[A-Za-z0-9._-]{1,20}$')]
    [string]$AccountName = 'KaoyanKiosk',

    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'KaoyanFocus'),

    [switch]$RemoveAccount,

    [switch]$RemoveApplication
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.Common.ps1')

if (-not (Test-IsAdministrator)) {
    throw 'Run this script from Windows PowerShell as an administrator.'
}

$targetDescription = 'remove the Assigned Access policy'
if ($RemoveAccount) { $targetDescription += ", delete local account '$AccountName' and its profile" }
if ($RemoveApplication) { $targetDescription += ", and delete '$InstallDirectory'" }
if (-not $PSCmdlet.ShouldProcess($env:COMPUTERNAME, $targetDescription)) {
    return
}

$deploymentDirectory = Join-Path $env:ProgramData 'KaoyanFocusKiosk'
New-Item -ItemType Directory -Path $deploymentDirectory -Force | Out-Null
$workerPath = Join-Path $deploymentDirectory 'Invoke-AssignedAccessSystem.ps1'
if (-not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AssignedAccessSystem.ps1') -Destination $workerPath -Force
}

Invoke-AssignedAccessWorker -Action Remove -WorkerPath $workerPath -WorkingDirectory $deploymentDirectory | Out-Null

if ($RemoveAccount) {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name.Split('\')[-1]
    if ($currentUser -eq $AccountName) {
        throw "Cannot remove the currently signed-in account '$AccountName'. Sign in as an administrator first."
    }

    $user = Get-LocalUser -Name $AccountName -ErrorAction SilentlyContinue
    if ($user) {
        $userSid = $user.SID.Value
        $profile = Get-CimInstance Win32_UserProfile -ErrorAction SilentlyContinue |
            Where-Object { $_.SID -eq $userSid }
        Remove-LocalUser -Name $AccountName
        if ($profile) {
            $profile | Remove-CimInstance
        }
    }
}

if ($RemoveApplication -and (Test-Path -LiteralPath $InstallDirectory)) {
    $resolvedInstallDirectory = (Resolve-Path -LiteralPath $InstallDirectory).Path
    $programFilesRoot = (Resolve-Path -LiteralPath $env:ProgramFiles).Path
    if (-not $resolvedInstallDirectory.StartsWith($programFilesRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an application directory outside Program Files: $resolvedInstallDirectory"
    }

    Remove-Item -LiteralPath $resolvedInstallDirectory -Recurse -Force
}

$shortcutPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Kaoyan Focus.lnk'
if ($RemoveApplication) {
    Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Assigned Access was removed successfully.' -ForegroundColor Green
Write-Host 'Restart Windows before testing the unrestricted account.'
