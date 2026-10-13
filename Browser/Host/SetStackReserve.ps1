param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $Path).Path
$bytes = [IO.File]::ReadAllBytes($resolved)
if ($bytes.Length -lt 256 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5A4D) { throw 'Invalid executable' }
$pe = [BitConverter]::ToInt32($bytes, 60)
$optional = $pe + 24
if ($pe -lt 64 -or $optional + 112 -gt $bytes.Length -or
    [BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664 -or
    [BitConverter]::ToUInt16($bytes, $optional) -ne 0x20B) { throw 'Expected Windows x64 PE executable' }
# Match Chromium's 8 MiB stack reservation for the self-hosted renderer process.
[BitConverter]::GetBytes([UInt64]8388608).CopyTo($bytes, $optional + 72)
[BitConverter]::GetBytes([UInt32]0).CopyTo($bytes, $optional + 64)
[IO.File]::WriteAllBytes($resolved, $bytes)
Write-Output 'Browser host stack reserve: 8 MiB'
