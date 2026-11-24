# Baut IP-Umschalter.exe mit dem C#-Compiler aus .NET Framework 4.x (in Windows enthalten).
# Aufruf: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$ico  = Join-Path $src 'app.ico'
$exe  = Join-Path $root 'IP-Umschalter.exe'
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

Add-Type -AssemblyName System.Drawing

# --- Symbol: blaues Rechteck mit "IP", als ICO mit 32-bit-DIB-Eintraegen (16-256 px) ---
function New-IconFrame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [Math]::Max(2, [int]($s * 0.2)); $d = 2 * $r
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $p.AddArc(0, 0, $d, $d, 180, 90); $p.AddArc($w - $d, 0, $d, $d, 270, 90)
    $p.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $p.AddArc(0, $w - $d, $d, $d, 90, 90); $p.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 111, 217))), $p)
    $font = New-Object System.Drawing.Font 'Segoe UI', ([float]($s * 0.5)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString('IP', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ([float]($s * 0.03)), $s, $s), $sf)
    $g.Dispose()
    # BITMAPINFOHEADER + BGRA-Zeilen von unten + AND-Maske (leer)
    $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bmp.Dispose()
    return ,$ms.ToArray()
}
$sizes = 16, 20, 24, 32, 48, 64, 256
$frames = foreach ($s in $sizes) { ,(New-IconFrame $s) }
$out = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $out
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$frames[$i].Length); $bw.Write([int]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
[System.IO.File]::WriteAllBytes($ico, $out.ToArray())

# --- Kompilieren ---
$files = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$ico" "/resource:$ico,IpUmschalter.app.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll /r:System.Xml.dll `
    $files
if ($LASTEXITCODE -ne 0) { throw "Kompilieren fehlgeschlagen ($LASTEXITCODE)" }
"Gebaut: $exe"
