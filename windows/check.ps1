<#
.SYNOPSIS
  Runs the same checks as CI (.github/workflows/windows.yml).

.DESCRIPTION
    .\check.ps1                     everything: format, build, smoke
    .\check.ps1 format [-Fix]       dotnet format --verify-no-changes (-Fix reformats in place)
    .\check.ps1 build               debug build + unit tests (warnings are errors), then the release
                                    app the smoke test uses (out\smoke-app\Convert.exe)
    .\check.ps1 smoke [-NoBuild] [smoke-test options...]
                                    rebuild the release app, then run smoke-test.ps1
                                    (-Strict, -RequireLibreOffice, -Out DIR, -Keep, -TimeoutSec N;
                                    see Get-Help .\smoke-test.ps1)

  Works from any folder. The release app is a plain dotnet publish, not build.ps1's package: the
  smoke test only needs Convert.exe.
#>
param(
    [string]$Step = "all",
    [switch]$Fix,
    [switch]$NoBuild
)
# Not an advanced script on purpose: everything after the known options is passed on to
# smoke-test.ps1 as typed (@args keeps "-Strict" a switch).

# Not "Stop": with output redirected, Windows PowerShell turns every line a native program writes to
# stderr into an error, and Stop would end the script there. Failures are found by exit code instead.
$ErrorActionPreference = "Continue"

$SdkVersion = "10.0.404"   # the one CI installs; dotnet format comes with the SDK, so it decides the formatting
$Root = $PSScriptRoot
$Solution = Join-Path $Root "Convert.slnx"
$AppProject = Join-Path $Root "src\Convert\Convert.csproj"
$AppOut = Join-Path $Root "out\smoke-app"
$AppExe = Join-Path $AppOut "Convert.exe"
$Rest = $args

function Say([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Find-Dotnet {
    $cmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    $default = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $default) { return $default }
    throw ".NET SDK not found. Install it with: winget install Microsoft.DotNet.SDK.10"
}

# Runs dotnet; on failure, stops with $Message (what to do about it).
function Invoke-Dotnet([string]$Message) {
    & $script:Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw $Message }
}

function Assert-NoArgs([string]$Name) {
    if ($Rest.Count -eq 0) { return }
    Write-Host "unknown option for ${Name}: $($Rest -join ' ')"
    exit 2
}

# dotnet format wants the line endings this machine writes (CRLF); a checkout with LF ones (Git's
# core.autocrlf=false) fails on every line, so say that instead of leaving a wall of diffs to puzzle over.
function Get-LfFiles {
    Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object { $_.Extension -eq ".cs" -and $_.FullName -notmatch '\\(bin|obj|out)\\' } |
        Where-Object { [regex]::IsMatch([IO.File]::ReadAllText($_.FullName), '(?<!\r)\n') }
}

function Test-Format {
    $version = (& $script:Dotnet --version | Out-String).Trim()
    if ($version -ne $SdkVersion) {
        Write-Warning ".NET SDK $version here, CI uses $SdkVersion; dotnet format results may differ."
    }
    if ($Fix) {
        Say "dotnet format (in place)"
        Invoke-Dotnet "dotnet format failed." format $Solution
        return
    }
    Say "dotnet format --verify-no-changes"
    & $script:Dotnet format $Solution --verify-no-changes
    if ($LASTEXITCODE -eq 0) { return }
    $lf = @(Get-LfFiles)
    if ($lf.Count -gt 0) {
        throw "$($lf.Count) files have LF line endings, and dotnet format wants CRLF. Git for Windows checks out CRLF by default (core.autocrlf=true); set that and check the files out again."
    }
    throw "Formatting is off. Fix it with: .\check.ps1 format -Fix"
}

function Build-ReleaseApp {
    Say "dotnet publish src\Convert -c Release (warnings are errors) -> out\smoke-app"
    # Files left over from an older build must not end up in the test.
    if (Test-Path -LiteralPath $AppOut) { Remove-Item -LiteralPath $AppOut -Recurse -Force -ErrorAction Stop }
    Invoke-Dotnet "The release build failed." publish $AppProject -c Release -o $AppOut -warnaserror -nologo

    Say "Convert.exe --cli lists the formats"
    # Piped, so that PowerShell waits for the GUI program. Only the targets line is shown: the usage
    # lines have a UTF-8 ellipsis that a console in another code page garbles.
    $list = @(& $AppExe --cli | ForEach-Object { "$_" })
    $targets = $list | Where-Object { $_ -like "targets: *" } | Select-Object -First 1
    if (-not $targets -or $targets -notmatch '\bmp3\b') {
        $list | ForEach-Object { Write-Host $_ }
        throw "The app didn't list its formats."
    }
    Write-Host $targets
}

function Test-Build {
    Say "dotnet build (warnings are errors)"
    Invoke-Dotnet "The build failed (warnings count as errors)." build $Solution -c Debug -warnaserror -nologo
    Say "dotnet test"
    Invoke-Dotnet "Unit tests failed." test $Solution -c Debug --no-build
    Build-ReleaseApp
}

function Test-Smoke([bool]$Build) {
    if ($Build) { Build-ReleaseApp }
    Say "smoke test"
    & (Join-Path $Root "smoke-test.ps1") @Rest -Exe $AppExe
    if ($LASTEXITCODE -ne 0) { throw "The smoke test failed (exit $LASTEXITCODE)." }
}

try {
    $script:Dotnet = Find-Dotnet
    switch ($Step) {
        "format" { Assert-NoArgs "format"; Test-Format }
        "build" { Assert-NoArgs "build"; Test-Build }
        "smoke" { Test-Smoke (-not $NoBuild) }
        "all" {
            Test-Format
            Test-Build
            Test-Smoke $false
            Say "All checks passed"
        }
        default {
            Write-Host "unknown step: $Step (format, build, smoke, or nothing for all; Get-Help $PSCommandPath)"
            exit 2
        }
    }
} catch {
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
exit 0
