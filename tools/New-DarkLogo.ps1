#Requires -Version 5.1
<#
.SYNOPSIS
  Genere assets\logo-dark.png : variante du logo Facil pour fond sombre (mode sombre GitHub, interface).
  Le texte noir devient clair ; le vert de marque et la transparence sont conserves a l'identique.
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\logo.png'),
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\logo-dark.png')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Bitmap]::FromFile($Source)
$dst = New-Object System.Drawing.Bitmap $src.Width, $src.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
    $changed = 0
    for ($y = 0; $y -lt $src.Height; $y++) {
        for ($x = 0; $x -lt $src.Width; $x++) {
            $c = $src.GetPixel($x, $y)
            # Pixel « neutre » (gris/noir, peu sature) = texte : on inverse sa luminosite. Le vert (sature) est garde.
            $max = [Math]::Max($c.R, [Math]::Max($c.G, $c.B)); $min = [Math]::Min($c.R, [Math]::Min($c.G, $c.B))
            if ($c.A -gt 0 -and ($max - $min) -lt 40) {
                $v = 255 - [int](($c.R + $c.G + $c.B) / 3)
                $v = [Math]::Max(200, $v)          # texte clair, jamais gris moyen (lisibilite)
                $dst.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c.A, $v, $v, [Math]::Min(255, $v + 6)))
                $changed++
            }
            else { $dst.SetPixel($x, $y, $c) }
        }
    }
    $dst.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
    "logo-dark.png : {0}x{1}, {2} pixels de texte eclaircis" -f $dst.Width, $dst.Height, $changed
}
finally { $src.Dispose(); $dst.Dispose() }
