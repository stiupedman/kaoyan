#requires -Version 5.1

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-KaoyanAssignedAccessConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath,

        [Parameter(Mandatory)]
        [string]$AccountName,

        [Parameter(Mandatory)]
        [Guid]$ProfileId
    )

    $escapedExecutable = [Security.SecurityElement]::Escape($ExecutablePath)
    $escapedAccount = [Security.SecurityElement]::Escape($AccountName)
    $formattedProfileId = '{' + $ProfileId.ToString().ToUpperInvariant() + '}'

    @"
<?xml version="1.0" encoding="utf-8"?>
<AssignedAccessConfiguration
    xmlns="http://schemas.microsoft.com/AssignedAccess/2017/config"
    xmlns:rs5="http://schemas.microsoft.com/AssignedAccess/201810/config"
    xmlns:v5="http://schemas.microsoft.com/AssignedAccess/2022/config">
  <Profiles>
    <Profile Id="$formattedProfileId" Name="KaoyanFocus">
      <AllAppsList>
        <AllowedApps>
          <App DesktopAppPath="$escapedExecutable" rs5:AutoLaunch="true" />
        </AllowedApps>
      </AllAppsList>
      <v5:StartPins><![CDATA[
        {"pinnedList":[{"desktopAppLink":"%ALLUSERSPROFILE%\\Microsoft\\Windows\\Start Menu\\Programs\\Kaoyan Focus.lnk"}]}
      ]]></v5:StartPins>
      <Taskbar ShowTaskbar="false" />
    </Profile>
  </Profiles>
  <Configs>
    <Config>
      <Account>$escapedAccount</Account>
      <DefaultProfile Id="$formattedProfileId" />
    </Config>
  </Configs>
</AssignedAccessConfiguration>
"@
}

function Invoke-AssignedAccessWorker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Apply', 'Remove')]
        [string]$Action,

        [Parameter(Mandatory)]
        [string]$WorkerPath,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [string]$ConfigurationPath
    )

    if (-not (Test-Path -LiteralPath $WorkerPath -PathType Leaf)) {
        throw "Assigned Access worker not found: $WorkerPath"
    }

    New-Item -ItemType Directory -Path $WorkingDirectory -Force | Out-Null
    $resultPath = Join-Path $WorkingDirectory 'last-result.json'
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue

    $quote = {
        param([string]$Value)
        "'" + $Value.Replace("'", "''") + "'"
    }

    $command = "& $(& $quote $WorkerPath) -Action $Action -ResultPath $(& $quote $resultPath)"
    if ($ConfigurationPath) {
        $command += " -ConfigurationPath $(& $quote $ConfigurationPath)"
    }

    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $taskName = "KaoyanFocusKiosk-$([Guid]::NewGuid().ToString('N'))"
    $taskAction = New-ScheduledTaskAction -Execute $powershell -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encodedCommand"
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

    try {
        Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName

        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        while (-not (Test-Path -LiteralPath $resultPath -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }

        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            throw 'Timed out waiting for the LocalSystem Assigned Access worker.'
        }

        $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $result.Success) {
            throw "Assigned Access $Action failed: $($result.Message)"
        }

        $result
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}
