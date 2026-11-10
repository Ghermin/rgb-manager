# Génère assets/icon.ico : trois disques rouge, vert et bleu mélangés comme de la lumière
# (jaune, cyan et magenta aux croisements, blanc au centre) sur un carré sombre arrondi.
# À lancer avec PowerShell 7 :  pwsh -File assets/make-icon.ps1
$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

public static class RgbIcon
{
    static bool InRoundedSquare(double x, double y, double m, double r)
    {
        double lo = m + r, hi = 1 - m - r;
        double cx = Math.Max(lo, Math.Min(hi, x)), cy = Math.Max(lo, Math.Min(hi, y));
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r && x >= m && x <= 1 - m && y >= m && y <= 1 - m;
    }

    static bool InCircle(double x, double y, double cx, double cy, double r) =>
        (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;

    public static byte[] RenderPng(int size)
    {
        const int ss = 6; // suréchantillonnage pour des bords lisses
        double margin = size <= 24 ? 0.0 : 0.03, radius = 0.22, cr = 0.255;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        for (int py = 0; py < size; py++)
        for (int px = 0; px < size; px++)
        {
            double a = 0, r = 0, g = 0, b = 0;
            for (int sy = 0; sy < ss; sy++)
            for (int sx = 0; sx < ss; sx++)
            {
                double x = (px + (sx + 0.5) / ss) / size, y = (py + (sy + 0.5) / ss) / size;
                if (!InRoundedSquare(x, y, margin, radius)) continue;
                double cr8 = 22, cg8 = 24, cb8 = 32; // fond
                if (InCircle(x, y, 0.500, 0.385, cr)) { cr8 += 255; cg8 += 40; cb8 += 50; }
                if (InCircle(x, y, 0.372, 0.605, cr)) { cr8 += 30; cg8 += 230; cb8 += 60; }
                if (InCircle(x, y, 0.628, 0.605, cr)) { cr8 += 40; cg8 += 90; cb8 += 255; }
                a += 1; r += Math.Min(255, cr8); g += Math.Min(255, cg8); b += Math.Min(255, cb8);
            }
            int n = ss * ss;
            if (a == 0) { bmp.SetPixel(px, py, Color.Transparent); continue; }
            bmp.SetPixel(px, py, Color.FromArgb((int)(255 * a / n), (int)(r / a), (int)(g / a), (int)(b / a)));
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        bmp.Dispose();
        return ms.ToArray();
    }
}
'@

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , [RgbIcon]::RenderPng($s) }

# Format ICO : en-tête, une entrée de 16 octets par taille, puis les images PNG
$out = Join-Path $PSScriptRoot 'icon.ico'
$ms = [IO.MemoryStream]::new()
$w = [IO.BinaryWriter]::new($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes($out, $ms.ToArray())
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon-256.png'), $images[-1])
Write-Host "OK : $out ($([math]::Round((Get-Item $out).Length / 1KB)) Ko)"
