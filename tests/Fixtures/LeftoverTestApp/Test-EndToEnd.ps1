<#
.SYNOPSIS
    End-to-end test: install "Leftover Test App", uninstall it with DeepUninstall, and verify that
    every leftover its sloppy uninstaller leaves behind was removed.

.DESCRIPTION
    Runs entirely per-user, so it needs no administrator rights. Requires Inno Setup 6
    (winget install JRSoftware.InnoSetup) and the .NET 9 SDK.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\Fixtures\LeftoverTestApp\Test-EndToEnd.ps1
#>
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$appName = "Leftover Test App"

function Find-Iscc {
    @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

$desktop = [Environment]::GetFolderPath("Desktop")
$checks = @(
    @{ Name = "Install folder (with logs)";  Test = { Test-Path "$env:LOCALAPPDATA\Programs\$appName" } }
    @{ Name = "AppData\Roaming settings";    Test = { Test-Path "$env:APPDATA\$appName" } }
    @{ Name = "AppData\Local cache";         Test = { Test-Path "$env:LOCALAPPDATA\$appName" } }
    @{ Name = "Desktop shortcut";            Test = { Test-Path "$desktop\$appName.lnk" } }
    @{ Name = "HKCU\Software\<app>";         Test = { Test-Path "HKCU:\Software\$appName" } }
    @{ Name = "HKCU\Software\<pub>\<app>";   Test = { Test-Path "HKCU:\Software\DeepUninstall Tests\$appName" } }
    @{ Name = "Empty publisher key";         Test = { Test-Path "HKCU:\Software\DeepUninstall Tests" } }
    @{ Name = "Startup (Run) entry";         Test = { $null -ne (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name $appName -ErrorAction SilentlyContinue) } }
    @{ Name = "File type (ProgID)";          Test = { Test-Path "HKCU:\Software\Classes\LeftoverTestApp.Document" } }
    @{ Name = "Open-with entry";             Test = { $null -ne (Get-ItemProperty "HKCU:\Software\Classes\.lefttest\OpenWithProgids" -Name "LeftoverTestApp.Document" -ErrorAction SilentlyContinue) } }
    @{ Name = "App Paths entry";             Test = { Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\LeftoverTestApp.exe" } }
    @{ Name = "Uninstall registration";      Test = { Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F1C2B7E-5A4D-4E3B-8C6F-1A2B3C4D5E6F}_is1" } }
)

Write-Host "1. Building the CLI and the test app installer..." -ForegroundColor Cyan
dotnet build (Join-Path $root "src\DeepUninstall.Cli") -c Debug -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CLI build failed." }
$cli = Join-Path $root "src\DeepUninstall.Cli\bin\Debug\net9.0-windows\deepuninstall.exe"
$iscc = Find-Iscc
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }
& $iscc /Q (Join-Path $PSScriptRoot "LeftoverTestApp.iss")
if ($LASTEXITCODE -ne 0) { throw "Fixture build failed." }

Write-Host "2. Installing $appName for the current user..." -ForegroundColor Cyan
$setup = Join-Path $root "artifacts\fixtures\LeftoverTestApp-Setup.exe"
$p = Start-Process $setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Fixture install failed with exit code $($p.ExitCode)." }

$missing = $checks | Where-Object { -not (& $_.Test) -and $_.Name -ne "Empty publisher key" }
if ($missing) { throw "Install did not create: $(($missing | ForEach-Object Name) -join ', ')" }
Write-Host "   All $($checks.Count) test items exist." -ForegroundColor DarkGray

Write-Host "3. Uninstalling with DeepUninstall..." -ForegroundColor Cyan
& $cli uninstall $appName --silent --yes --no-restore-point
$cliExit = $LASTEXITCODE

Write-Host "`n4. Verifying..." -ForegroundColor Cyan
$failed = 0
foreach ($check in $checks) {
    if (& $check.Test) {
        Write-Host ("   FAIL  {0} is still there" -f $check.Name) -ForegroundColor Red
        $failed++
    } else {
        Write-Host ("   PASS  {0} removed" -f $check.Name) -ForegroundColor Green
    }
}

Write-Host ""
if ($failed -eq 0 -and $cliExit -eq 0) {
    Write-Host "END-TO-END TEST PASSED: all $($checks.Count) leftovers were removed." -ForegroundColor Green
    exit 0
}
Write-Host "END-TO-END TEST FAILED: $failed item(s) left behind, CLI exit code $cliExit." -ForegroundColor Red
exit 1
