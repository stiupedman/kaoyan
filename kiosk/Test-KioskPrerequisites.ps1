#requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$ThrowOnFailure
)

$ErrorActionPreference = 'Stop'

$currentVersion = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$editionId = [string]$currentVersion.EditionID
$buildNumber = [int]$currentVersion.CurrentBuildNumber
$displayVersion = [string]$currentVersion.DisplayVersion
$productName = [string]$currentVersion.ProductName
$uacEnabled = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA -eq 1

$supportedEdition = $editionId -match '^(Professional|ProfessionalEducation|ProfessionalWorkstation|Enterprise|EnterpriseS|Education|IoTEnterprise|IoTEnterpriseS)'
$supportedBuild = $buildNumber -ge 22621
$reasons = [System.Collections.Generic.List[string]]::new()

if (-not $supportedEdition) {
    $reasons.Add("Windows edition '$editionId' does not support Assigned Access. Upgrade to Windows 11 Pro, Enterprise, Education, or IoT Enterprise.")
}

if (-not $supportedBuild) {
    $reasons.Add("Windows build $buildNumber is too old. Windows 11 22H2 (build 22621) or later is required by this configuration.")
}

if (-not $uacEnabled) {
    $reasons.Add('User Account Control (UAC) must be enabled for Assigned Access.')
}

$result = [pscustomobject]@{
    ProductName      = $productName
    EditionId        = $editionId
    DisplayVersion   = $displayVersion
    BuildNumber      = $buildNumber
    UacEnabled       = $uacEnabled
    SupportedEdition = $supportedEdition
    SupportedBuild   = $supportedBuild
    Ready             = $reasons.Count -eq 0
    Reasons           = $reasons.ToArray()
}

if ($ThrowOnFailure -and -not $result.Ready) {
    throw ($result.Reasons -join [Environment]::NewLine)
}

$result
