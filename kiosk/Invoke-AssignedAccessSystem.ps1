#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Apply', 'Remove')]
    [string]$Action,

    [string]$ConfigurationPath,

    [Parameter(Mandatory)]
    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.User.Value -ne 'S-1-5-18') {
        throw 'This worker must run as LocalSystem (SID S-1-5-18).'
    }

    $instance = Get-CimInstance -Namespace 'root\cimv2\mdm\dmmap' -ClassName 'MDM_AssignedAccess'
    if ($Action -eq 'Apply') {
        if (-not $ConfigurationPath -or -not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
            throw 'A valid Assigned Access XML configuration path is required.'
        }

        $configuration = Get-Content -LiteralPath $ConfigurationPath -Raw -Encoding UTF8
        [xml]$configuration | Out-Null
        $instance.Configuration = [Net.WebUtility]::HtmlEncode($configuration)
    }
    else {
        $instance.Configuration = $null
    }

    Set-CimInstance -CimInstance $instance | Out-Null
    $result = [pscustomobject]@{
        Success = $true
        Message = "Assigned Access configuration action '$Action' completed."
    }
}
catch {
    $result = [pscustomobject]@{
        Success = $false
        Message = $_.Exception.Message
    }
}

$resultDirectory = Split-Path -Parent $ResultPath
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$result | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding UTF8

if (-not $result.Success) {
    exit 1
}
