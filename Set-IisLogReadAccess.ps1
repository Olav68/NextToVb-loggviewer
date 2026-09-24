[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$LogPath = 'C:\inetpub\logs\LogFiles',
    [string]$AppPoolName = 'LoggViewer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Dette scriptet må kjøres på Windows.'
}

$principal = "IIS AppPool\$AppPoolName"
$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'

if (-not (Test-Path -LiteralPath $LogPath -PathType Container)) {
    throw "Loggmappen finnes ikke: $LogPath"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principalObject = [Security.Principal.WindowsPrincipal]$identity
$administrator = [Security.Principal.WindowsBuiltInRole]::Administrator
if (-not $principalObject.IsInRole($administrator)) {
    throw 'Kjør PowerShell som administrator for å endre NTFS-rettigheter.'
}

# OI/CI sørger for at både eksisterende og nye filer/mapper arver lese- og kjørerettighet.
$grant = '{0}:(OI)(CI)(RX)' -f $principal
$arguments = @(
    $LogPath,
    '/grant:r',
    $grant,
    '/T',
    '/C'
)

if ($PSCmdlet.ShouldProcess($LogPath, "Gi $principal arvet RX-tilgang")) {
    & $icacls @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "icacls feilet med exit code $LASTEXITCODE."
    }

    Write-Host "Lese-/kjørerettighet satt for $principal på $LogPath og underliggende filer."
    Write-Host 'Nye filer arver rettighetene via OI/CI.'
    Write-Host 'LoggViewer leser med FileShare.ReadWrite | FileShare.Delete, slik at IIS kan fortsette å skrive og rotere filer.'
}
