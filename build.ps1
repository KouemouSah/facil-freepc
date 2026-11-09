#Requires -Version 5.1
<#
.SYNOPSIS
  Construction locale de Facil FreePC : compilation (avertissements = erreurs) -> tests -> publication
  d'un exe unique autonome -> installeur Inno Setup -> empreinte SHA-256.
  Meme sequence que .github\workflows\release.yml (a activer plus tard).
.PARAMETER SkipTests
  A n'utiliser que pour un essai rapide : une version distribuee doit TOUJOURS passer les tests.
.PARAMETER Iscc
  Chemin de ISCC.exe ; par defaut : variable INNO_ISCC, puis Inno Setup 7, puis 6.
#>
[CmdletBinding()]
param([switch]$SkipTests, [string]$Iscc)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

function Step([string]$Name) { Write-Host "`n== $Name" -ForegroundColor Cyan }
function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Exe)) a echoue (code $LASTEXITCODE)" }
}

# Version : source unique Directory.Build.props
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version invalide dans Directory.Build.props : '$version'" }

if (-not $Iscc) {
    $Iscc = @($env:INNO_ISCC,
        'C:\Program Files\Inno Setup 7\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw 'ISCC.exe introuvable : installez Inno Setup ou passez -Iscc' }

Write-Host "Facil FreePC $version  |  ISCC : $Iscc"
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

Step 'Compilation (Release, avertissements = erreurs)'
Invoke-Checked 'dotnet' @('build', (Join-Path $root 'FreeRes.sln'), '-c', 'Release', '-nologo', '-v', 'q')

if ($SkipTests) { Write-Warning 'Tests IGNORES (-SkipTests) : ne pas distribuer cette version.' }
else {
    Step 'Tests'
    Invoke-Checked 'dotnet' @('test', (Join-Path $root 'FreeRes.sln'), '-c', 'Release', '--no-build', '-nologo', '-v', 'q')
}

Step 'Publication (exe unique autonome win-x64)'
Invoke-Checked 'dotnet' @('publish', (Join-Path $root 'src\FreeRes.App\FreeRes.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish, '-nologo', '-v', 'q')
$exe = Join-Path $publish 'FacilFreePC.exe'
if (-not (Test-Path $exe)) { throw "Exe publie introuvable : $exe" }
$extra = @(Get-ChildItem $publish -File | Where-Object { $_.Name -ne 'FacilFreePC.exe' })
if ($extra.Count -gt 0) { throw "La publication doit produire un exe unique ; fichiers en trop : $($extra.Name -join ', ')" }

Step 'Installeur Inno Setup'
Invoke-Checked $Iscc @('/Q', "/DAppVersion=$version", "/DSourceExe=$exe", (Join-Path $root 'installer\FacilFreePC.iss'))
$setup = Join-Path $artifacts "FacilFreePC-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "Installeur introuvable : $setup" }

Step 'Empreintes SHA-256'
foreach ($f in $exe, $setup) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$f.sha256", "$h  $([IO.Path]::GetFileName($f))`n", (New-Object Text.UTF8Encoding($false)))
    "{0}  {1}  ({2:N1} MB)" -f $h, [IO.Path]::GetFileName($f), ((Get-Item $f).Length / 1MB)
}
Write-Host "`nOK : $setup" -ForegroundColor Green
