#Requires -Version 5.1
<#
.SYNOPSIS
  Essai de l'installeur dans le Bac a sable Windows (Windows 11 vierge, efface a la fermeture).
  Construit deux installeurs d'ESSAI (version inferieure et superieure, meme exe) pour verifier le refus du retour
  en arriere et la mise a jour silencieuse avec relance, lance le bac a sable avec Test-Installer.ps1, puis attend
  son compte rendu.
.PARAMETER Setup
  Installeur a essayer (par defaut : le plus recent de artifacts).
.NOTES
  Prerequis : fonctionnalite « Containers-DisposableClientVM » (Bac a sable Windows) activee.
#>
[CmdletBinding()]
param([string]$Setup, [int]$TimeoutMinutes = 15)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$sandboxExe = Join-Path $env:windir 'System32\WindowsSandbox.exe'
if (-not (Test-Path $sandboxExe)) { throw 'Bac a sable Windows absent : activer la fonctionnalite Containers-DisposableClientVM (droits administrateur, redemarrage).' }
if (-not $Setup) { $Setup = (Get-ChildItem $artifacts -Filter 'FacilFreePC-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
if (-not $Setup -or -not (Test-Path $Setup)) { throw 'Installeur introuvable : lancer build.ps1' }

$version = [regex]::Match([IO.Path]::GetFileName($Setup), '(\d+)\.(\d+)\.(\d+)')
$major, $minor, $patch = [int]$version.Groups[1].Value, [int]$version.Groups[2].Value, [int]$version.Groups[3].Value
$older = if ($patch -gt 0) { "$major.$minor.$($patch - 1)" } else { "$major.$($minor - 1).99" }
$newer = "$major.$minor.$($patch + 1)"

$run = Join-Path $artifacts ('sandbox-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$inputs = Join-Path $run 'entree'
$results = Join-Path $run 'resultats'
New-Item -ItemType Directory -Force $inputs, $results | Out-Null
Copy-Item $Setup $inputs
Copy-Item (Join-Path $PSScriptRoot 'Test-Installer.ps1') $inputs

# Installeurs d'essai : meme exe publie, seule la version de l'installeur change.
$iscc = @($env:INNO_ISCC, 'C:\Program Files\Inno Setup 7\ISCC.exe', 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe') | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
$xpi = Get-ChildItem (Join-Path $artifacts 'extension') -Filter *.xpi -ErrorAction SilentlyContinue | Select-Object -First 1
foreach ($v in $older, $newer) {
    & $iscc /Q "/DAppVersion=$v" "/DSourceExe=$(Join-Path $artifacts 'publish\FacilFreePC.exe')" "/DExtensionXpi=$($xpi.FullName)" "/O$inputs" (Join-Path $root 'installer\FacilFreePC.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC a echoue pour la version d'essai $v" }
}

$name = [IO.Path]::GetFileName($Setup)
$command = "powershell -NoProfile -ExecutionPolicy Bypass -File C:\entree\Test-Installer.ps1 -Setup C:\entree\$name " +
    "-Older C:\entree\FacilFreePC-Setup-$older.exe -Newer C:\entree\FacilFreePC-Setup-$newer.exe -Report C:\resultats\compte-rendu.log"
$wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputs</HostFolder><SandboxFolder>C:\entree</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$results</HostFolder><SandboxFolder>C:\resultats</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>$command</Command></LogonCommand>
</Configuration>
"@
$wsbFile = Join-Path $run 'essai.wsb'
Set-Content -Path $wsbFile -Value $wsb -Encoding UTF8
Write-Host "Bac a sable : $wsbFile (versions $older < $($version.Value) < $newer)"
Start-Process $sandboxExe -ArgumentList "`"$wsbFile`""

$report = Join-Path $results 'compte-rendu.log'
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $report) -and (Select-String -Path $report -Pattern '^Echecs : ' -Quiet)) { break }
    Start-Sleep -Seconds 5
}
if (-not (Test-Path $report)) { throw "Aucun compte rendu apres $TimeoutMinutes min : $results" }
Get-Content $report
