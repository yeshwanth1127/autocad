<#
.SYNOPSIS
  Manipulates autocad-final trial storage (same encoding as Licensing/TrialExpiry.cs) for manual NETLOAD testing.

.DESCRIPTION
  Writes HKCU\Software\autocad-final\Runtime\d and %LocalAppData%\.ac_plg\s.dat + %AppData%\.ac_plg\s.dat
  using the same SHA256+XOR+Base64 payload as the plugin.

.PARAMETER Mode
  ClearStore    - remove trial state (next NETLOAD behaves like first run).
  ShowStore     - decode and print registry + files; estimate expired vs 7-day window.
  StampNow      - set start = UTC now (trial just started).
  StampExpired  - set start = 8 days ago (plugin should block on next load/command).
  StampEdge     - set start = exactly 7 days + 1 second ago (just past boundary).

.EXAMPLE
  .\scripts\test-trial.ps1 -Mode ShowStore

.EXAMPLE
  .\scripts\test-trial.ps1 -Mode StampExpired
  # Then NETLOAD autocad-final.dll in AutoCAD - expect expiry message, no palette.
#>

param(
    [string] $Mode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validModes = @('ClearStore', 'ShowStore', 'StampNow', 'StampExpired', 'StampEdge')
if ([string]::IsNullOrWhiteSpace($Mode)) {
    Write-Error ('Missing -Mode. Valid: ' + ($validModes -join ', ')) -ErrorAction Stop
}
if ($validModes -notcontains $Mode) {
    Write-Error ("Invalid -Mode '{0}'. Valid: {1}" -f $Mode, ($validModes -join ', ')) -ErrorAction Stop
}

$salt = 'autocad-final|trial|EA04F805-C87E-4C73-AFFF-9B94F1230C30'
$regPath = 'HKCU:\Software\autocad-final\Runtime'
$valueName = 'd'
$localFile = Join-Path $env:LOCALAPPDATA '.ac_plg\s.dat'
$roamFile  = Join-Path $env:APPDATA '.ac_plg\s.dat'
$trialDays = 7

function Get-XorKeyBytes {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($salt))
    }
    finally {
        if ($sha) { $sha.Dispose() }
    }
}

function Encode-StartUtc([datetime] $utc) {
    $ticks = $utc.ToUniversalTime().Ticks
    $buf = [BitConverter]::GetBytes([int64]$ticks)
    $key = Get-XorKeyBytes
    for ($i = 0; $i -lt $buf.Length; $i++) {
        $buf[$i] = $buf[$i] -bxor $key[$i % $key.Length]
    }
    [Convert]::ToBase64String($buf)
}

function TryDecode-StartUtc([string] $encoded) {
    try {
        $buf = [Convert]::FromBase64String($encoded.Trim())
        if ($buf.Length -ne 8) { return $null }
        $key = Get-XorKeyBytes
        for ($i = 0; $i -lt $buf.Length; $i++) {
            $buf[$i] = $buf[$i] -bxor $key[$i % $key.Length]
        }
        $ticks = [BitConverter]::ToInt64($buf, 0)
        return [datetime]::new($ticks, 'Utc')
    }
    catch {
        return $null
    }
}

function Write-AllStores([string] $payload) {
    if (-not (Test-Path -LiteralPath (Split-Path $regPath -Parent))) {
        New-Item -Path (Split-Path $regPath -Parent) -Force | Out-Null
    }
    New-Item -Path $regPath -Force | Out-Null
    Set-ItemProperty -Path $regPath -Name $valueName -Value $payload -Type String

    foreach ($p in @($localFile, $roamFile)) {
        $dir = [System.IO.Path]::GetDirectoryName($p)
        if (-not [string]::IsNullOrEmpty($dir) -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        if (Test-Path -LiteralPath $p) {
            try {
                $fi = Get-Item -LiteralPath $p -Force
                $fi.Attributes = [System.IO.FileAttributes]::Normal
            }
            catch { }
        }
        [System.IO.File]::WriteAllText($p, $payload, [System.Text.UTF8Encoding]::new($false))
        try {
            $f = Get-Item -LiteralPath $p -Force
            $f.Attributes = $f.Attributes -bor [System.IO.FileAttributes]::Hidden
            $d = Get-Item -LiteralPath $dir -Force
            $d.Attributes = $d.Attributes -bor [System.IO.FileAttributes]::Hidden
        }
        catch {
            # optional attributes
        }
    }
}

function Remove-AllStores {
    Remove-Item -Path $regPath -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $localFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $roamFile -Force -ErrorAction SilentlyContinue
    $ld = [System.IO.Path]::GetDirectoryName($localFile)
    $rd = [System.IO.Path]::GetDirectoryName($roamFile)
    if ((Test-Path -LiteralPath $ld) -and -not (Get-ChildItem -LiteralPath $ld -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $ld -Force -ErrorAction SilentlyContinue
    }
    if ((Test-Path -LiteralPath $rd) -and -not (Get-ChildItem -LiteralPath $rd -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $rd -Force -ErrorAction SilentlyContinue
    }
}

function Read-RegPayload {
    if (-not (Test-Path -LiteralPath $regPath)) { return $null }
    $v = (Get-ItemProperty -Path $regPath -Name $valueName -ErrorAction SilentlyContinue).$valueName
    if ([string]::IsNullOrEmpty($v)) { return $null }
    return [string]$v
}

function Read-FilePayload([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false)).Trim()
}

function Show-State {
    Write-Host '--- Registry ---' -ForegroundColor Cyan
    $rp = Read-RegPayload
    if ($rp) {
        $dt = TryDecode-StartUtc $rp
        if ($dt) {
            Write-Host "  $valueName = (decoded UTC $($dt.ToString('o')))"
            Show-ExpiryEstimate $dt
        }
        else {
            Write-Host "  $valueName = present but decode failed"
        }
    }
    else {
        Write-Host '  (missing or empty)'
    }

    foreach ($pair in @(@{ Name = 'LocalAppData'; Path = $localFile }, @{ Name = 'AppData'; Path = $roamFile })) {
        Write-Host "--- File $($pair.Name) ---" -ForegroundColor Cyan
        $fp = Read-FilePayload $pair.Path
        if ($fp) {
            $dt = TryDecode-StartUtc $fp
            if ($dt) {
                Write-Host "  $($pair.Path)"
                Write-Host "  decoded UTC $($dt.ToString('o'))"
                Show-ExpiryEstimate $dt
            }
            else {
                Write-Host "  present but decode failed"
            }
        }
        else {
            Write-Host '  (missing)'
        }
    }

    $now = [datetime]::UtcNow
    Write-Host "--- Plugin rule (same as C#) ---" -ForegroundColor Cyan
    Write-Host "  Expired when: UtcNow > startUtc.AddDays($trialDays)"
    Write-Host "  UtcNow = $($now.ToString('o'))"
}

function Show-ExpiryEstimate([datetime] $startUtc) {
    $end = $startUtc.AddDays($trialDays)
    $now = [datetime]::UtcNow
    $expired = $now -gt $end
    Write-Host "  Trial ends (UTC): $($end.ToString('o'))"
    if ($expired) {
        Write-Host '  Status: EXPIRED (plugin should block).' -ForegroundColor Red
    }
    else {
        $left = $end - $now
        $hrs = [math]::Floor($left.TotalHours)
        Write-Host "  Status: ACTIVE - about $hrs hours left." -ForegroundColor Green
    }
}

switch ($Mode) {
    'ClearStore' {
        Remove-AllStores
        Write-Host 'Trial storage cleared. Next NETLOAD starts a new trial clock.' -ForegroundColor Green
    }
    'ShowStore' {
        Show-State
    }
    'StampNow' {
        $start = [datetime]::UtcNow
        $enc = Encode-StartUtc $start
        Write-AllStores $enc
        Write-Host ('Set start UTC = ' + $start.ToUniversalTime().ToString('o') + ' (trial active).') -ForegroundColor Green
        Show-State
    }
    'StampExpired' {
        $start = [datetime]::UtcNow.AddDays(-8)
        $enc = Encode-StartUtc $start
        Write-AllStores $enc
        Write-Host ('Set start UTC = ' + $start.ToUniversalTime().ToString('o') + " (older than $trialDays days).") -ForegroundColor Yellow
        Show-State
    }
    'StampEdge' {
        $start = [datetime]::UtcNow.AddDays(-$trialDays).AddSeconds(-1)
        $enc = Encode-StartUtc $start
        Write-AllStores $enc
        Write-Host ('Set start UTC = ' + $start.ToUniversalTime().ToString('o') + (' (just past ' + $trialDays + '-day boundary).')) -ForegroundColor Yellow
        Show-State
    }
}
