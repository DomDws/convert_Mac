<#
  Shared by build.ps1 and uninstall.ps1 (dot-sourced; not meant to be run on its own).

  Everything that touches the signing certificate, the machine-wide trust store, the install folder or
  the registered package lives here, so the two scripts can't drift apart.
#>

$ConvertPublisher = 'CN=Convert Dev'
$ConvertPackageName = 'Convert'
$CodeSigningOid = '1.3.6.1.5.5.7.3.3'

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Note([string]$Text) { Write-Host "    $Text" }

# --- folders -----------------------------------------------------------------------------------------

# The known-folder API, not $env:LOCALAPPDATA: an empty or odd environment variable must never turn into
# a relative path or a drive root that later gets deleted.
function Get-LocalAppDataDir {
    $dir = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($dir) -or -not [IO.Path]::IsPathRooted($dir)) {
        throw "Windows didn't report a local application data folder for this account, so Convert doesn't know where it is installed."
    }
    $dir = [IO.Path]::GetFullPath($dir).TrimEnd('\')
    if ($dir.Length -le 3 -or -not [IO.Directory]::Exists($dir)) {
        throw "The local application data folder '$dir' doesn't look right (missing or a drive root); refusing to continue."
    }
    return $dir
}

function Get-ConvertInstallDir { Join-Path (Get-LocalAppDataDir) 'Programs\Convert' }
function Get-ConvertDataDir { Join-Path (Get-LocalAppDataDir) 'Convert' }

function Test-PathUnder([string]$Path, [string]$Parent) {
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    return $p.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)
}

# Deletes a folder that must lie strictly inside $Parent. Never follows junctions or symbolic links (it
# removes the link, not what it points to; Windows PowerShell's Remove-Item -Recurse can follow them).
# Retries while files are in use. Returns $true when the folder is gone.
function Remove-DirectorySafely {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Parent, [int]$Attempts = 1)

    if (-not [IO.Path]::IsPathRooted($Path) -or -not [IO.Path]::IsPathRooted($Parent)) {
        throw "Internal error: refusing to delete relative path '$Path'."
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not (Test-PathUnder $full $Parent)) { throw "Internal error: refusing to delete '$full', which isn't inside '$Parent'." }
    if (-not [IO.Directory]::Exists($full)) { return $true }
    if (-not $PSCmdlet.ShouldProcess($full, 'Delete folder')) { return $false }

    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            Remove-TreeNoFollow $full
            return $true
        } catch {
            if ($i -eq $Attempts) { Write-Verbose "Deleting $full failed: $($_.Exception.Message)"; return $false }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Remove-TreeNoFollow([string]$Dir) {
    $info = New-Object IO.DirectoryInfo $Dir
    if ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) { $info.Delete(); return }   # just the link
    foreach ($entry in $info.GetFileSystemInfos()) {
        if ($entry -is [IO.DirectoryInfo]) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { $entry.Delete() } else { Remove-TreeNoFollow $entry.FullName }
        } else {
            if ($entry.Attributes -band [IO.FileAttributes]::ReadOnly) { $entry.Attributes = $entry.Attributes -band -bnot [IO.FileAttributes]::ReadOnly }
            $entry.Delete()
        }
    }
    $info.Delete()
}

# --- running processes -------------------------------------------------------------------------------

# Convert.exe started from the install folder, and the COM surrogates (dllhost.exe) that hold the
# context-menu DLL from it.
function Get-ProcessesUsingDir([string]$Dir) {
    $prefix = [IO.Path]::GetFullPath($Dir).TrimEnd('\') + '\'
    $found = @()
    foreach ($p in @(Get-Process -Name Convert, dllhost -ErrorAction SilentlyContinue)) {
        try {
            if ($p.ProcessName -eq 'Convert') {
                if ($p.Path -and $p.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $found += $p }
            } else {
                foreach ($m in $p.Modules) {
                    if ($m.FileName -and $m.FileName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $found += $p; break }
                }
            }
        } catch { }   # another user's or an elevated process: can't be ours
    }
    return $found
}

function Stop-ProcessesUsingDir {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$Dir)
    foreach ($p in @(Get-ProcessesUsingDir $Dir)) {
        if ($PSCmdlet.ShouldProcess("$($p.ProcessName) (pid $($p.Id))", 'Stop process')) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Write-Note "Stopped $($p.ProcessName) (pid $($p.Id))" }
            catch { Write-Warning "Couldn't stop $($p.ProcessName) (pid $($p.Id)): $($_.Exception.Message)" }
        }
    }
}

# --- packages ----------------------------------------------------------------------------------------

function Import-AppxModule {
    # The Appx cmdlets are Windows PowerShell only; PowerShell 7 reaches them through its compatibility layer.
    if ($PSVersionTable.PSEdition -eq 'Core' -and -not (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue)) {
        Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue
    }
}

# Only our package: same name AND our publisher (another app may well be called "Convert").
function Get-ConvertPackage {
    Import-AppxModule
    @(Get-AppxPackage -Name $ConvertPackageName -ErrorAction SilentlyContinue | Where-Object { $_.Publisher -eq $ConvertPublisher })
}

# --- certificates ------------------------------------------------------------------------------------

# A "CN=Convert Dev" certificate as these scripts create it: self-issued, for code signing.
function Test-ConvertDevCert($Cert) {
    if ($Cert.Subject -ne $ConvertPublisher -or $Cert.Issuer -ne $ConvertPublisher) { return $false }
    foreach ($ext in $Cert.Extensions) {
        if ($ext -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $ext.EnhancedKeyUsages) { if ($oid.Value -eq $CodeSigningOid) { return $true } }
        }
    }
    return $false
}

# Reads a store without needing elevation (LocalMachine stores are readable by everyone).
function Get-ConvertDevCerts([string]$Location, [string]$Name) {
    $store = New-Object Security.Cryptography.X509Certificates.X509Store($Name, $Location)
    try { $store.Open([Security.Cryptography.X509Certificates.OpenFlags]'ReadOnly, OpenExistingOnly') }
    catch { return @() }   # the store doesn't exist (e.g. CurrentUser\TrustedPeople is often missing)
    try { return @($store.Certificates | Where-Object { Test-ConvertDevCert $_ }) }
    finally { $store.Close() }
}

function Assert-Thumbprint([string]$Thumbprint) {
    if ($Thumbprint -notmatch '^[0-9A-F]{40}$') { throw "Internal error: '$Thumbprint' is not a certificate thumbprint." }
}

# Describes where a certificate's private key lives, so its deletion can be checked afterwards.
function Get-PrivateKeyInfo($Cert) {
    if (-not $Cert.HasPrivateKey) { return $null }
    $key = $null
    try {
        $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Cert)
        if ($key -is [Security.Cryptography.RSACng]) {
            return [pscustomobject]@{ Kind = 'CNG'; Name = $key.Key.KeyName; Provider = $key.Key.Provider.Provider }
        }
        if ($key -is [Security.Cryptography.RSACryptoServiceProvider]) {
            $c = $key.CspKeyContainerInfo
            return [pscustomobject]@{ Kind = 'CAPI'; Name = $c.KeyContainerName; Provider = $c.ProviderName; ProviderType = $c.ProviderType; MachineKey = $c.MachineKeyStore }
        }
    } catch {
    } finally {
        if ($key) { $key.Dispose() }
    }
    return [pscustomobject]@{ Kind = 'unknown'; Name = $null }
}

function Test-PrivateKeyExists($Info) {
    if (-not $Info -or -not $Info.Name) { return $false }
    if ($Info.Kind -eq 'CNG') {
        return [Security.Cryptography.CngKey]::Exists($Info.Name, (New-Object Security.Cryptography.CngProvider $Info.Provider))
    }
    if ($Info.Kind -eq 'CAPI') {
        $p = New-Object Security.Cryptography.CspParameters($Info.ProviderType, $Info.Provider, $Info.Name)
        $p.Flags = [Security.Cryptography.CspProviderFlags]::UseExistingKey
        if ($Info.MachineKey) { $p.Flags = $p.Flags -bor [Security.Cryptography.CspProviderFlags]::UseMachineKeyStore }
        try { $csp = New-Object Security.Cryptography.RSACryptoServiceProvider (, $p); $csp.Dispose(); return $true } catch { return $false }
    }
    return $false
}

function Remove-PrivateKeyDirectly($Info) {
    if ($Info.Kind -eq 'CNG') {
        [Security.Cryptography.CngKey]::Open($Info.Name, (New-Object Security.Cryptography.CngProvider $Info.Provider)).Delete()
    } elseif ($Info.Kind -eq 'CAPI') {
        $p = New-Object Security.Cryptography.CspParameters($Info.ProviderType, $Info.Provider, $Info.Name)
        $p.Flags = [Security.Cryptography.CspProviderFlags]::UseExistingKey
        $csp = New-Object Security.Cryptography.RSACryptoServiceProvider (, $p)
        $csp.PersistKeyInCsp = $false
        $csp.Clear()
    }
}

# Removes a certificate from one of the current user's stores, together with its private key and key
# container. Returns a short description of what's left ('' when nothing is).
function Remove-UserCert {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$StoreName, [Parameter(Mandatory)][string]$Thumbprint)
    Assert-Thumbprint $Thumbprint
    $path = "Cert:\CurrentUser\$StoreName\$Thumbprint"
    $cert = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if (-not $cert) { return '' }
    if (-not $PSCmdlet.ShouldProcess("$path (and its private key)", 'Delete certificate')) { return "certificate $Thumbprint in CurrentUser\$StoreName" }
    $key = Get-PrivateKeyInfo $cert
    # A copy that points at a key deleted earlier (e.g. the CurrentUser\CA copy) can't be removed with
    # -DeleteKey, hence the second, plain attempt.
    if ($key -and ($key.Kind -eq 'unknown' -or (Test-PrivateKeyExists $key))) {
        try { Remove-Item -LiteralPath $path -DeleteKey -ErrorAction Stop } catch { Write-Verbose "Remove-Item -DeleteKey $path failed: $($_.Exception.Message)" }
    }
    if (Test-Path -LiteralPath $path) {
        try { Remove-Item -LiteralPath $path -ErrorAction Stop } catch { Write-Verbose "Remove-Item $path failed: $($_.Exception.Message)" }
    }
    if ($key -and (Test-PrivateKeyExists $key)) {
        try { Remove-PrivateKeyDirectly $key } catch { Write-Verbose "Deleting key $($key.Name) failed: $($_.Exception.Message)" }
    }
    $left = @()
    if (Test-Path -LiteralPath $path) { $left += "certificate $Thumbprint in CurrentUser\$StoreName" }
    if ($key -and $key.Kind -eq 'unknown') { $left += "possibly the private key of $Thumbprint (not an RSA key, so its deletion couldn't be checked)" }
    elseif ($key -and (Test-PrivateKeyExists $key)) { $left += "private key container '$($key.Name)' ($($key.Provider))" }
    return ($left -join ', ')
}

# --- running something elevated ----------------------------------------------------------------------

# A PowerShell single-quoted string literal for any text. Doubling is the only escape inside '...';
# PowerShell also treats the typographic quotes as single quotes, so those are doubled too.
function ConvertTo-PSLiteral([string]$Text) {
    # Written as code points so this file stays ASCII (Windows PowerShell reads BOM-less files as ANSI).
    $quotes = '[' + (-join ([char[]](0x27, 0x2018, 0x2019, 0x201A, 0x201B))) + ']'
    return "'" + ($Text -replace $quotes, '$0$0') + "'"
}

# Command line for a child Windows PowerShell that runs $Script. The script travels as -EncodedCommand
# (Base64 of UTF-16), so nothing in it is ever re-parsed by a command line or by cmd; every value in it
# must be embedded with ConvertTo-PSLiteral. -EncodedCommand isn't subject to the execution policy.
function Get-ChildPowerShellCommand([string]$Script) {
    $exe = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    return [pscustomobject]@{ FilePath = $exe; Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded" }
}

# Runs $Script elevated (one UAC prompt) and waits. Returns Started/Cancelled/ExitCode/Message.
function Invoke-ElevatedPowerShell([string]$Script) {
    $cmd = Get-ChildPowerShellCommand $Script
    try {
        $p = Start-Process -FilePath $cmd.FilePath -ArgumentList $cmd.Arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop
        return [pscustomobject]@{ Started = $true; Cancelled = $false; ExitCode = $p.ExitCode; Message = '' }
    } catch {
        $e = $_.Exception
        $cancelled = $false
        while ($e) {
            if ($e -is [ComponentModel.Win32Exception] -and $e.NativeErrorCode -eq 1223) { $cancelled = $true }
            $e = $e.InnerException
        }
        if ($_.Exception.Message -match 'canceled by the user|cancelled by the user') { $cancelled = $true }
        return [pscustomobject]@{ Started = $false; Cancelled = $cancelled; ExitCode = $null; Message = $_.Exception.Message }
    }
}

# Script (run elevated) that changes LocalMachine\TrustedPeople: adds the given DER certificates (Base64)
# and removes the given thumbprints. It only ever touches "CN=Convert Dev" certificates.
# Exit codes: 0 done, 2 a certificate to add isn't ours, 1 anything else failed.
function New-TrustStoreScript([string[]]$AddBase64 = @(), [string[]]$RemoveThumbprints = @()) {
    foreach ($b in $AddBase64) { if ($b -notmatch '^[A-Za-z0-9+/]+={0,2}$') { throw 'Internal error: certificate data is not Base64.' } }
    foreach ($t in $RemoveThumbprints) { Assert-Thumbprint $t }
    $add = ($AddBase64 | ForEach-Object { ConvertTo-PSLiteral $_ }) -join ', '
    $remove = ($RemoveThumbprints | ForEach-Object { ConvertTo-PSLiteral $_ }) -join ', '
    $publisher = ConvertTo-PSLiteral $ConvertPublisher
    return @"
`$ErrorActionPreference = 'Stop'
try {
    `$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'LocalMachine')
    `$store.Open('ReadWrite')
    try {
        foreach (`$b in @($add)) {
            `$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (, [System.Convert]::FromBase64String(`$b))
            if (`$c.Subject -ne $publisher -or `$c.HasPrivateKey) { exit 2 }
            `$store.Add(`$c)
        }
        foreach (`$t in @($remove)) {
            foreach (`$c in @(`$store.Certificates.Find('FindByThumbprint', `$t, `$false))) {
                if (`$c.Subject -eq $publisher) { `$store.Remove(`$c) }
            }
        }
    } finally { `$store.Close() }
    exit 0
} catch { exit 1 }
"@
}
