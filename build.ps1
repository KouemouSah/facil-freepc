#Requires -Version 5.1
<#
.SYNOPSIS
  Construction locale de Facil FreePC : compilation (avertissements = erreurs) -> tests -> publication
  d'un exe unique autonome -> installeur Inno Setup -> empreinte SHA-256 -> manifeste de mise a jour signe.
  Meme sequence que .github\workflows\release.yml (a activer plus tard).
.PARAMETER SkipTests
  A n'utiliser que pour un essai rapide : une version distribuee doit TOUJOURS passer les tests.
.PARAMETER Iscc
  Chemin de ISCC.exe ; par defaut : variable INNO_ISCC, puis Inno Setup 7, puis 6.
.PARAMETER Notes
  Notes de version (une phrase) affichees dans la decision de mise a jour.
#>
[CmdletBinding()]
param([switch]$SkipTests, [string]$Iscc, [string]$Notes = 'Corrections et améliorations : voir les notes de version sur GitHub.')

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

Step 'Extension Firefox (tests Node, validation Mozilla, paquet)'
# Prerequis : Node.js (tests de la politique de mise en veille + outil officiel web-ext de Mozilla).
$extDir = Join-Path $root 'extensions\firefox'
$webExt = 'web-ext@10.8.0'   # version figee : construction reproductible
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js est requis pour construire et tester l''extension Firefox' }
# Version du manifeste = version du produit (source unique : Directory.Build.props).
$manifestPath = Join-Path $extDir 'manifest.json'
$manifestText = [IO.File]::ReadAllText($manifestPath)
$synced = [regex]::Replace($manifestText, '"version":\s*"[^"]*"', "`"version`": `"$version`"", 1)
if ($synced -ne $manifestText) { [IO.File]::WriteAllText($manifestPath, $synced, (New-Object Text.UTF8Encoding($false))); Write-Host "   manifest.json -> $version" }
if (-not $SkipTests) { Invoke-Checked 'node' @('--test', (Join-Path $extDir 'test')) }
Invoke-Checked 'npx' @('--yes', $webExt, 'lint', '--source-dir', $extDir, '--ignore-files', 'test/**', '--warnings-as-errors', '--output', 'text')
$extOut = Join-Path $artifacts 'extension'
New-Item -ItemType Directory -Force -Path $extOut | Out-Null
$xpi = Join-Path $extOut 'facil-freepc-firefox.xpi'
if ($env:AMO_JWT_ISSUER -and $env:AMO_JWT_SECRET) {
    # Signature Mozilla (canal « unlisted » : non publie sur le catalogue, installable par Firefox standard).
    Invoke-Checked 'npx' @('--yes', $webExt, 'sign', '--channel', 'unlisted', '--source-dir', $extDir, '--artifacts-dir', $extOut,
        '--ignore-files', 'test/**', '--api-key', $env:AMO_JWT_ISSUER, '--api-secret', $env:AMO_JWT_SECRET)
    $signed = Get-ChildItem $extOut -Filter '*.xpi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $signed) { throw 'Signature Mozilla : aucun .xpi produit' }
    Move-Item $signed.FullName $xpi -Force
    Write-Host '   extension SIGNEE par Mozilla'
}
else {
    Invoke-Checked 'npx' @('--yes', $webExt, 'build', '--source-dir', $extDir, '--artifacts-dir', $extOut, '--ignore-files', 'test/**', '--overwrite-dest', '--filename', 'facil-freepc-firefox.xpi')
    Write-Warning 'Extension NON signee (AMO_JWT_ISSUER / AMO_JWT_SECRET absents) : Firefox standard refusera de l''installer ; FreePC ne la proposera pas.'
}

Step 'Publication (exe unique autonome win-x64)'
Invoke-Checked 'dotnet' @('publish', (Join-Path $root 'src\FreeRes.App\FreeRes.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish, '-nologo', '-v', 'q')
$exe = Join-Path $publish 'FacilFreePC.exe'
if (-not (Test-Path $exe)) { throw "Exe publie introuvable : $exe" }
$extra = @(Get-ChildItem $publish -File | Where-Object { $_.Name -ne 'FacilFreePC.exe' })
if ($extra.Count -gt 0) { throw "La publication doit produire un exe unique ; fichiers en trop : $($extra.Name -join ', ')" }

Step 'Installeur Inno Setup'
Invoke-Checked $Iscc @('/Q', "/DAppVersion=$version", "/DSourceExe=$exe", "/DExtensionXpi=$xpi", (Join-Path $root 'installer\FacilFreePC.iss'))
$setup = Join-Path $artifacts "FacilFreePC-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "Installeur introuvable : $setup" }

Step 'Empreintes SHA-256'
foreach ($f in $exe, $setup) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$f.sha256", "$h  $([IO.Path]::GetFileName($f))`n", (New-Object Text.UTF8Encoding($false)))
    "{0}  {1}  ({2:N1} MB)" -f $h, [IO.Path]::GetFileName($f), ((Get-Item $f).Length / 1MB)
}

Step 'Manifeste de mise a jour (latest.json signe)'
# L'application n'installe une mise a jour que si ce manifeste est signe par la cle de Facil (cle PUBLIQUE compilee
# dans l'exe) et si l'installeur telecharge a cette empreinte. Cle privee : UPDATE_SIGNING_KEY (CI) ou fichier
# protege par DPAPI sur le poste de publication.
$sign = @('run', '--project', (Join-Path $root 'tools\FreeRes.UpdateSign\FreeRes.UpdateSign.csproj'), '-c', 'Release', '--')
$setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Join-Path $artifacts 'latest.json'
$url = "https://github.com/KouemouSah/facil-freepc/releases/download/v$version/FacilFreePC-Setup-$version.exe"
Invoke-Checked 'dotnet' ($sign + @('manifest', $version, $url, $setupHash, $Notes, $manifest))
$keyFile = Join-Path $env:APPDATA 'FacilFreePC-signing\update-key.dpapi'
if ($env:UPDATE_SIGNING_KEY -or (Test-Path $keyFile)) {
    Invoke-Checked 'dotnet' ($sign + @('sign', $manifest))
}
else {
    Remove-Item "$manifest.sig" -ErrorAction SilentlyContinue
    Write-Warning 'Manifeste NON signe (cle absente) : les versions installees ignoreront cette publication.'
}

Write-Host "`nOK : $setup" -ForegroundColor Green
