#Requires -Version 5.1
<#
.SYNOPSIS
  Essai de l'installeur sur une machine VIERGE (Bac a sable Windows ou runner GitHub Actions). Ne pas lancer sur
  un poste ou Facil FreePC est installe pour de bon : le script installe, lance, arrete et DESINSTALLE.
.PARAMETER Setup
  Installeur a essayer (FacilFreePC-Setup-x.y.z.exe).
.PARAMETER Older
  Installeur d'une version PLUS ANCIENNE : son installation doit etre refusee par-dessus Setup.
.PARAMETER Newer
  Installeur d'une version PLUS RECENTE : mise a jour silencieuse avec /RELAUNCH=1, donnees conservees, relance.
.PARAMETER SkipLaunch
  Sans lancement de l'application (environnement sans session interactive).
.PARAMETER Report
  Fichier de compte rendu (une ligne par verification).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Setup,
    [string]$Older,
    [string]$Newer,
    [switch]$SkipLaunch,
    [string]$Report = (Join-Path $PSScriptRoot 'test-installer.log'))

$ErrorActionPreference = 'Stop'
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\Facil FreePC'
$exe = Join-Path $appDir 'FacilFreePC.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'FacilFreePC'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{930648CE-F8E4-4C30-8D5E-B717E90CFCEA}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:failures = 0
Set-Content -Path $Report -Value "Essai de l'installeur - $(Get-Date -Format s) - $env:COMPUTERNAME" -Encoding UTF8

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $line = '{0}  {1}{2}' -f ($(if ($Ok) { 'OK   ' } else { 'ECHEC' })), $Name, ($(if ($Detail) { " ($Detail)" } else { '' }))
    Add-Content -Path $Report -Value $line -Encoding UTF8
    Write-Host $line -ForegroundColor ($(if ($Ok) { 'Green' } else { 'Red' }))
    if (-not $Ok) { $script:failures++ }
}

function Install([string]$Installer, [string[]]$Extra = @()) {
    $p = Start-Process -FilePath $Installer -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') + $Extra) -Wait -PassThru
    return $p.ExitCode
}

function Version() { (Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).DisplayVersion }

function WaitFor([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return [bool](& $Condition)
}

function Running() { [bool](Get-Process FacilFreePC -ErrorAction SilentlyContinue) }

function Quit() {
    if (Test-Path $exe) { Start-Process -FilePath $exe -ArgumentList '--quit' -Wait }
    WaitFor { -not (Running) } 20 | Out-Null
}

# ---------------------------------------------------------------- installation
$expected = [regex]::Match([IO.Path]::GetFileName($Setup), '\d+\.\d+\.\d+').Value
Check 'Installation silencieuse' ((Install $Setup) -eq 0)
Check 'Executable installe pour l''utilisateur' (Test-Path $exe) $exe
Check 'Version enregistree' ((Version) -eq $expected) "$(Version)"
Check 'Demarrage a l''ouverture de session' ([bool](Get-ItemProperty $runKey -ErrorAction SilentlyContinue).FacilFreePC)

# ---------------------------------------------------------------- premier lancement
if (-not $SkipLaunch) {
    Start-Process -FilePath $exe -ArgumentList '--background'
    Check 'Lancement en arriere-plan' (WaitFor { Running } 20)
    Check 'Base de donnees creee' (WaitFor { Test-Path (Join-Path $dataDir 'freeres.db') } 60)
    Check 'Journal ecrit' (WaitFor { @(Get-ChildItem (Join-Path $dataDir 'logs') -Filter *.jsonl -ErrorAction SilentlyContinue).Count -gt 0 } 60)
    Quit
    Check 'Arret propre (--quit)' (-not (Running))
}

# ---------------------------------------------------------------- retour en arriere refuse
if ($Older) {
    $code = Install $Older
    Check 'Version plus ancienne refusee' ($code -ne 0 -and (Version) -eq $expected) "code $code, version $(Version)"
}

# ---------------------------------------------------------------- mise a jour comme depuis l'application
if ($Newer) {
    $newVersion = [regex]::Match([IO.Path]::GetFileName($Newer), '\d+\.\d+\.\d+').Value
    if (-not $SkipLaunch) { Start-Process -FilePath $exe -ArgumentList '--background'; WaitFor { Running } 20 | Out-Null }
    $code = Install $Newer @('/RELAUNCH=1')
    Check 'Mise a jour silencieuse' ($code -eq 0 -and (Version) -eq $newVersion) "code $code, version $(Version)"
    Check 'Donnees conservees' (Test-Path (Join-Path $dataDir 'freeres.db'))
    if (-not $SkipLaunch) {
        Check 'Relance apres mise a jour' (WaitFor { Running } 30)
        Quit
    }
}

# ---------------------------------------------------------------- desinstallation
Quit
$uninstaller = Join-Path $appDir 'unins000.exe'
Check 'Desinstalleur present' (Test-Path $uninstaller)
if (Test-Path $uninstaller) {
    $p = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    Check 'Desinstallation silencieuse' ($p.ExitCode -eq 0)
    # Le desinstalleur se copie et se termine de facon differee : on attend le retrait reel.
    Check 'Executable retire' (WaitFor { -not (Test-Path $exe) } 30)
    Check 'Inscription retiree' (WaitFor { -not (Test-Path $uninstallKey) } 30)
    Check 'Demarrage automatique retire' (-not [bool](Get-ItemProperty $runKey -ErrorAction SilentlyContinue).FacilFreePC)
    if (-not $SkipLaunch) { Check 'Donnees conservees (desinstallation silencieuse)' (Test-Path (Join-Path $dataDir 'freeres.db')) }
}

Add-Content -Path $Report -Value "Echecs : $script:failures" -Encoding UTF8
Write-Host "Echecs : $script:failures"
exit $script:failures
