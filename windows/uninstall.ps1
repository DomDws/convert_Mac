<#
.SYNOPSIS
  Removes Convert: the context-menu package, the installed files and the signing certificate.

.DESCRIPTION
    .\uninstall.ps1           remove the package, %LOCALAPPDATA%\Programs\Convert, every "CN=Convert Dev"
                              certificate with its private key from your stores, and the trust entry in
                              LocalMachine\TrustedPeople (one UAC prompt, only if there is one)
    .\uninstall.ps1 -All      also remove settings (%LOCALAPPDATA%\Convert) and Convert's temp files
    .\uninstall.ps1 -WhatIf   only show what would be removed

  It ends with a list of what was removed and what, if anything, is left. Safe to run again.
#>
[CmdletBinding(SupportsShouldProcess)]
param([switch]$All)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'packaging\Common.ps1')

$removed = New-Object Collections.Generic.List[string]
$left = New-Object Collections.Generic.List[string]
$would = [bool]$WhatIfPreference
function Removed([string]$What) { $removed.Add($What); if (-not $would) { Write-Note "Removed $What" } }

$localAppData = Get-LocalAppDataDir
$InstallDir = Get-ConvertInstallDir

# --- package and program files -----------------------------------------------------------------------

Write-Step 'Removing the context-menu package and the program'
Stop-ProcessesUsingDir $InstallDir
$packages = @()
try { $packages = @(Get-ConvertPackage) }
catch { $left.Add("the package, if installed (couldn't list packages: $($_.Exception.Message))") }
foreach ($pkg in $packages) {
    if (-not $PSCmdlet.ShouldProcess($pkg.PackageFullName, 'Remove package')) { $removed.Add("package $($pkg.PackageFullName)"); continue }
    try { Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop; Removed "package $($pkg.PackageFullName)" }
    catch { $left.Add("package $($pkg.PackageFullName) ($($_.Exception.Message))") }
}

if ([IO.Directory]::Exists($InstallDir)) {
    # dllhost may hold the context-menu DLL for a moment after the package is gone.
    Stop-ProcessesUsingDir $InstallDir
    if (Remove-DirectorySafely -Path $InstallDir -Parent $localAppData -Attempts 20) { Removed "program files ($InstallDir)" }
    elseif ($would) { $removed.Add("program files ($InstallDir)") }
    else { $left.Add("program files in $InstallDir (in use: close Explorer windows or sign out and back in, then run .\uninstall.ps1 again)") }
}

# --- certificates ------------------------------------------------------------------------------------

Write-Step 'Removing the signing certificate'
# The current user's stores: build.ps1 creates the certificate in My and Windows adds a copy to CA.
foreach ($storeName in 'My', 'CA', 'TrustedPeople') {
    foreach ($c in @(Get-ConvertDevCerts 'CurrentUser' $storeName)) {
        $what = "certificate $($c.Thumbprint) from CurrentUser\$storeName$(if ($c.HasPrivateKey) { ' with its private key' })"
        if ($would) { [void](Remove-UserCert -StoreName $storeName -Thumbprint $c.Thumbprint); $removed.Add($what); continue }
        $rest = Remove-UserCert -StoreName $storeName -Thumbprint $c.Thumbprint
        if ($rest) { $left.Add("$rest (delete it in certmgr.msc)") } else { Removed $what }
    }
}

# The machine-wide trust entry needs elevation; matched by thumbprint in the elevated step.
$machine = @(Get-ConvertDevCerts 'LocalMachine' 'TrustedPeople' | ForEach-Object { $_.Thumbprint })
if ($machine.Count) {
    $target = "LocalMachine\TrustedPeople: $($machine -join ', ')"
    if (-not $PSCmdlet.ShouldProcess($target, 'Remove trust entries (asks for permission)')) {
        foreach ($t in $machine) { $removed.Add("trust entry $t from LocalMachine\TrustedPeople") }
    } else {
        Write-Note "Removing $($machine.Count) trust entr$(if ($machine.Count -eq 1) { 'y' } else { 'ies' }) from LocalMachine\TrustedPeople (Windows asks for permission)"
        $result = Invoke-ElevatedPowerShell (New-TrustStoreScript -RemoveThumbprints $machine)
        $still = @(Get-ConvertDevCerts 'LocalMachine' 'TrustedPeople' | ForEach-Object { $_.Thumbprint })
        foreach ($t in $machine) {
            if ($still -notcontains $t) { Removed "trust entry $t from LocalMachine\TrustedPeople" }
            else {
                $why = if ($result.Cancelled) { 'you declined the permission prompt' } elseif (-not $result.Started) { $result.Message } else { "the elevated step failed with exit code $($result.ExitCode)" }
                $left.Add("trust entry $t in LocalMachine\TrustedPeople ($why). To finish, run .\uninstall.ps1 again and choose Yes, or in an administrator PowerShell: Remove-Item Cert:\LocalMachine\TrustedPeople\$t")
            }
        }
    }
}
# Not created by these scripts, so only reported.
foreach ($storeName in 'Root', 'CA', 'My', 'TrustedPublisher') {
    foreach ($c in @(Get-ConvertDevCerts 'LocalMachine' $storeName)) {
        $left.Add("certificate $($c.Thumbprint) in LocalMachine\$storeName (not created by build.ps1; remove it in certlm.msc if you added it)")
    }
}

# --- settings (-All) ---------------------------------------------------------------------------------

if ($All) {
    Write-Step 'Removing settings and temporary files'
    $dataDir = Get-ConvertDataDir
    if ([IO.Directory]::Exists($dataDir)) {
        if (Remove-DirectorySafely -Path $dataDir -Parent $localAppData -Attempts 6) { Removed "settings ($dataDir)" }
        elseif ($would) { $removed.Add("settings ($dataDir)") }
        else { $left.Add("settings in $dataDir (in use: close Convert and run .\uninstall.ps1 -All again)") }
    }
    # Only inside the user's own profile: an unusual TEMP must not point this at a shared folder.
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ($userProfile -and [IO.Path]::IsPathRooted($userProfile) -and (Test-PathUnder $temp $userProfile)) {
        $tempDir = Join-Path $temp 'Convert'
        if ([IO.Directory]::Exists($tempDir)) {
            if (Remove-DirectorySafely -Path $tempDir -Parent $temp -Attempts 3) { Removed "temporary files ($tempDir)" }
            elseif ($would) { $removed.Add("temporary files ($tempDir)") }
            else { $left.Add("temporary files in $tempDir (in use)") }
        }
        # Selection lists the context menu hands to Convert.exe for very long selections.
        foreach ($f in @(Get-ChildItem -LiteralPath $temp -Filter 'convert-*.txt' -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^convert-[0-9a-f]{32}\.txt$' })) {
            if ($PSCmdlet.ShouldProcess($f.FullName, 'Delete file')) {
                try { $f.Delete(); Removed "selection list $($f.FullName)" } catch { $left.Add("$($f.FullName) (in use)") }
            } else { $removed.Add("selection list $($f.FullName)") }
        }
    } else {
        $left.Add("Convert's temporary files, if any (the temp folder $temp isn't inside your profile, so it was left alone)")
    }
}

# --- summary -----------------------------------------------------------------------------------------

Write-Host ''
if ($removed.Count) {
    Write-Host $(if ($would) { 'Would remove:' } else { 'Removed:' }) -ForegroundColor Green
    foreach ($r in $removed) { Write-Host "  - $r" }
} else {
    Write-Host 'Nothing of Convert was found to remove.' -ForegroundColor Green
}
if ($left.Count) {
    Write-Host 'Left:' -ForegroundColor Yellow
    foreach ($l in $left) { Write-Host "  - $l" }
    if (-not $would) { exit 1 }
} elseif (-not $would) {
    $dataDir = Get-ConvertDataDir
    if (-not $All -and [IO.Directory]::Exists($dataDir)) { Write-Host "Nothing else is left. Your settings are kept in $dataDir (.\uninstall.ps1 -All removes them)." }
    else { Write-Host 'Nothing of Convert is left.' }
}
