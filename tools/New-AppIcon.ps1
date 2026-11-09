#Requires -Version 5.1
<#
.SYNOPSIS
  Genere assets\app.ico (multi-tailles, carre) et les images de l'assistant d'installation
  a partir de l'icone de marque fournie (une seule image 233x256, non carree).
  Windows choisit la taille adaptee (16 px zone de notification ... 256 px explorateur) :
  une icone a image unique non carree est floue ou deformee.
.PARAMETER Source
  Icone ou PNG source (non modifie).
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $env:USERPROFILE 'Documents\Automatisations\free_resources\icon.ico'),
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Get-SourceBitmap([string]$Path) {
    if ($Path -like '*.ico') {
        $ico = New-Object System.Drawing.Icon($Path, 256, 256)
        try { return $ico.ToBitmap() } finally { $ico.Dispose() }
    }
    return [System.Drawing.Bitmap]::FromFile($Path)
}

function New-SquareBitmap([System.Drawing.Image]$Img, [int]$Size) {
    # Centre l'image sur un carre transparent en conservant ses proportions.
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $scale = [Math]::Min($Size / $Img.Width, $Size / $Img.Height)
        $w = [int][Math]::Round($Img.Width * $scale); $h = [int][Math]::Round($Img.Height * $scale)
        $g.DrawImage($Img, [int](($Size - $w) / 2), [int](($Size - $h) / 2), $w, $h)
    }
    finally { $g.Dispose() }
    return $bmp
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$src = Get-SourceBitmap $Source
try {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $pngs = foreach ($s in $sizes) {
        $b = New-SquareBitmap $src $s
        try { $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray() }
        finally { $b.Dispose() }
    }
    # Format ICO : en-tete 6 octets + 16 octets par entree + images PNG (accepte par Windows Vista et suivants).
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length
        $dim = [byte]$(if ($s -ge 256) { 0 } else { $s })
        $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Flush()
    [IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $out.ToArray())
    $bw.Dispose()

    # Petite image de l'assistant Inno Setup (coin superieur droit) : 55x55 et 110x110 (ecrans haute densite).
    foreach ($s in 55, 110) {
        $b = New-SquareBitmap $src $s
        try { $b.Save((Join-Path $OutDir "wizard-small-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) } finally { $b.Dispose() }
    }
}
finally { $src.Dispose() }

$check = New-Object System.Drawing.Icon((Join-Path $OutDir 'app.ico'), 16, 16)
"app.ico : {0} tailles ({1}) ; controle 16 px -> {2}x{3}" -f $sizes.Count, ($sizes -join ','), $check.Width, $check.Height
$check.Dispose()
