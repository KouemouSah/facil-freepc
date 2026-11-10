<#
.SYNOPSIS
Installe Inno Setup a la version FIGEE du projet et renvoie le chemin de ISCC.exe.

.DESCRIPTION
Meme compilateur partout : la CI (release.yml) et les postes de developpement utilisent ce script, donc l'installeur
publie est construit par la version testee en local. Installeur officiel (depot jrsoftware/issrc) verifie AVANT
execution : empreinte SHA-256 epinglee + signature Authenticode valide du signataire attendu. Le fichier reste
verrouille en ecriture de la verification a la fin de l'execution (aucun remplacement possible entre les deux).
Ne fait rien si cette version est deja installee. Changer de version = mettre a jour les 3 constantes ET le minimum
exige par installer\FacilFreePC.iss.

.PARAMETER WorkDir
Dossier de telechargement (defaut : RUNNER_TEMP en CI, sinon artifacts\inno du depot).
#>
[CmdletBinding()]
param([string]$WorkDir)

$ErrorActionPreference = 'Stop'
$Version = '7.1.1'
$Url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe'
$Sha256 = '8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0'
$SignerThumbprint = 'E0AB19C8D38CBF9C44709925122A7A02F8C70CB7'   # Pyrsys B.V. (editeur d'Inno Setup)

function Get-InstalledIscc {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    $entry = Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'Inno Setup*' -and $_.DisplayVersion -eq $Version } |
        Select-Object -First 1
    if (-not $entry) { return $null }
    $iscc = Join-Path $entry.InstallLocation 'ISCC.exe'
    if (Test-Path -LiteralPath $iscc) { return $iscc }
    return $null
}

$iscc = Get-InstalledIscc
if ($iscc) {
    Write-Host "Inno Setup $Version deja installe : $iscc"
    return $iscc
}

if (-not $WorkDir) { $WorkDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\inno' } }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$setup = Join-Path $WorkDir ([IO.Path]::GetFileName($Url))
Write-Host "Telechargement Inno Setup $Version"
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $Url -OutFile $setup -UseBasicParsing

# Verrou : lecture partagee (l'execution reste possible), ecriture et suppression interdites jusqu'a la fin.
$lock = [IO.File]::Open($setup, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = -join ($sha.ComputeHash($lock) | ForEach-Object { $_.ToString('X2') }) } finally { $sha.Dispose() }
    if ($actual -ne $Sha256) { throw "Empreinte SHA-256 inattendue pour $setup : $actual (attendu $Sha256)" }
    $sig = Get-AuthenticodeSignature -LiteralPath $setup
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Thumbprint -ne $SignerThumbprint) {
        throw "Signature de l'installeur Inno Setup refusee : $($sig.Status) / $($sig.SignerCertificate.Thumbprint)"
    }
    $p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Installation d'Inno Setup $Version en echec (code $($p.ExitCode))" }
}
finally { $lock.Dispose() }

$iscc = Get-InstalledIscc
if (-not $iscc) { throw "Inno Setup $Version installe mais ISCC.exe introuvable" }
Write-Host "Inno Setup $Version installe : $iscc"
return $iscc
