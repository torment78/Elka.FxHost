<#
.SYNOPSIS
    Registers (or unregisters) the Elka FX Host virtual ASIO driver so Matrix and DAWs list it.

.DESCRIPTION
    ASIO hosts find drivers under HKLM\SOFTWARE\ASIO, so registration needs administrator
    rights. The script relaunches itself elevated when needed. Matrix loads the DLL from the
    path that was registered, so register the copy you want to test (by default the one the
    Visual Studio Debug build puts next to the app).

    Close Matrix (or stop its ASIO device) before rebuilding: Windows keeps the loaded DLL
    locked, and the build cannot overwrite it.

.EXAMPLE
    .\scripts\register-asio-driver.ps1
    .\scripts\register-asio-driver.ps1 -Channels 32
    .\scripts\register-asio-driver.ps1 -Unregister
#>
param(
    [string]$DriverPath,
    [ValidateSet(0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120, 128)]
    [int]$Channels = 0,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $DriverPath) {
    $candidates = @(
        (Join-Path $repoRoot 'src\app-wpf\bin\Debug\net8.0-windows\win-x64\ElkaFxHost.Asio.dll'),
        (Join-Path $repoRoot 'src\app-wpf\bin\Release\net8.0-windows\win-x64\ElkaFxHost.Asio.dll'),
        (Join-Path $repoRoot 'build-vs2026\Release\ElkaFxHost.Asio.dll'),
        (Join-Path $repoRoot 'build-vs2022\Release\ElkaFxHost.Asio.dll')
    )
    $DriverPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $DriverPath) {
        throw 'ElkaFxHost.Asio.dll not found. Build the solution in Visual Studio first (or pass -DriverPath).'
    }
}
$DriverPath = (Resolve-Path $DriverPath).Path

# The channel count is a per-user setting (HKCU), so set it before elevating.
if ($Channels -gt 0) {
    $key = 'HKCU:\Software\ElkaSoft\FxHost\Asio'
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name 'ChannelCount' -Value $Channels -Type DWord
    Write-Host "ASIO channel count set to $Channels."
}

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-DriverPath', "`"$DriverPath`"")
    if ($Unregister) { $arguments += '-Unregister' }
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

$regsvr = Join-Path $env:WINDIR 'System32\regsvr32.exe'
$regArgs = if ($Unregister) { @('/s', '/u', "`"$DriverPath`"") } else { @('/s', "`"$DriverPath`"") }
$result = Start-Process -FilePath $regsvr -ArgumentList $regArgs -Wait -PassThru
if ($result.ExitCode -ne 0) {
    throw "regsvr32 failed with exit code $($result.ExitCode)."
}

if ($Unregister) {
    Write-Host 'Elka FX Host ASIO driver unregistered.'
} else {
    Write-Host "Elka FX Host ASIO driver registered: $DriverPath"
    Write-Host 'Open Matrix and pick "Elka FX Host" as an ASIO device.'
}
