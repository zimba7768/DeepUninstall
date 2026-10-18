<#
.SYNOPSIS
    Builds DeepUninstall release files: the portable app, the CLI and the setup.exe installer.

.EXAMPLE
    .\installer\build.ps1 -Version 0.2.0

    Output goes to artifacts\:
      app\DeepUninstall.exe                 portable desktop app (single file)
      cli\deepuninstall.exe                 command-line tool (single file)
      installer\DeepUninstall-Setup-*.exe   installer
#>
param(
    [string]$Version = "0.0.0-dev",
    [string]$Iscc
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"
$numericVersion = ($Version -split '[-+]')[0]

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

$publishArgs = @("-c", "Release", "-r", "win-x64", "--self-contained",
    "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-p:Version=$Version", "-nologo")

Write-Host "Publishing desktop app..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\DeepUninstall.App") @publishArgs "-p:IncludeNativeLibrariesForSelfExtract=true" -o (Join-Path $artifacts "app")
if ($LASTEXITCODE -ne 0) { throw "Publishing the desktop app failed." }

Write-Host "Publishing CLI..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\DeepUninstall.Cli") @publishArgs -o (Join-Path $artifacts "cli")
if ($LASTEXITCODE -ne 0) { throw "Publishing the CLI failed." }

if (-not $Iscc) {
    $candidates = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) }
    $Iscc = $candidates | Select-Object -First 1
}
if (-not $Iscc) { throw "Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup" }

Write-Host "Building installer with $Iscc..." -ForegroundColor Cyan
& $Iscc "/Qp" "/DAppVersion=$Version" "/DAppVersionNumeric=$numericVersion" (Join-Path $PSScriptRoot "DeepUninstall.iss")
if ($LASTEXITCODE -ne 0) { throw "Building the installer failed." }

Get-ChildItem $artifacts -Recurse -Include *.exe | ForEach-Object {
    "{0,-60} {1,8:N1} MB" -f $_.FullName.Substring($root.Length + 1), ($_.Length / 1MB)
}
