# Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.
#
# Builds the Windows app icon (src/FileFluss.App/Assets/FileFluss.ico) from the macOS app icon.
#
# The Mac icon is a logo on a white rounded tile with macOS's margin around it; on Windows that
# shows as a small white square in the taskbar beside icons that fill their space. This cuts the
# logo off its tile and lets it fill the icon, as Windows icons do: the tile's light pixels that
# can be reached from outside the logo become transparent (its edge pixels are un-blended from
# the white, so the outline stays smooth), and the logo is scaled to fill the square.
#
#   powershell -ExecutionPolicy Bypass -File windows/Packaging/make-icon.ps1
#
# Run again whenever the Mac icon changes.

param(
    [string]$Source = "$PSScriptRoot\..\..\FileFluss\Resources\Assets.xcassets\AppIcon.appiconset\icon_1024x1024.png",
    [string]$Output = "$PSScriptRoot\..\src\FileFluss.App\Assets\FileFluss.ico",
    [double]$Margin = 0.03
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ("FileFlussIconCutter" -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class FileFlussIconCutter
{
    // The tile: white to light grey and colourless. The logo's outline is dark slate, so a
    // flood fill through light pixels from outside stops at it.
    static bool IsTile(byte r, byte g, byte b, byte a)
    {
        if (a < 128) return true;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return (r + g + b) / 3 > 200 && max - min < 28;
    }

    /// <summary>The logo cut off its tile, cropped to a square around it with the given margin.</summary>
    public static Bitmap Cut(Bitmap source, double margin)
    {
        int w = source.Width, h = source.Height;
        var px = new byte[w * h * 4];
        var data = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        source.UnlockBits(data);

        // Background: tile pixels connected to the image border.
        var background = new bool[w * h];
        var queue = new Queue<int>();
        for (int x = 0; x < w; x++) { queue.Enqueue(x); queue.Enqueue((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { queue.Enqueue(y * w); queue.Enqueue(y * w + w - 1); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (background[i]) continue;
            int o = i * 4;
            if (!IsTile(px[o + 2], px[o + 1], px[o], px[o + 3])) continue;
            background[i] = true;
            int x = i % w, y = i / w;
            if (x > 0) queue.Enqueue(i - 1);
            if (x < w - 1) queue.Enqueue(i + 1);
            if (y > 0) queue.Enqueue(i - w);
            if (y < h - 1) queue.Enqueue(i + w);
        }

        var result = new byte[px.Length];
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, o = i * 4;
                if (background[i]) continue;
                byte b = px[o], g = px[o + 1], r = px[o + 2];
                double alpha = px[o + 3] / 255.0;

                // Along the edge the logo was drawn over white: take the white back out.
                bool edge = (x > 0 && background[i - 1]) || (x < w - 1 && background[i + 1]) ||
                            (y > 0 && background[i - w]) || (y < h - 1 && background[i + w]);
                if (edge)
                {
                    double a = Math.Max(255 - r, Math.Max(255 - g, 255 - b)) / 255.0;
                    a = Math.Min(1.0, a / 0.75);
                    if (a <= 0.02) continue;
                    r = Unblend(r, a); g = Unblend(g, a); b = Unblend(b, a);
                    alpha *= a;
                }

                result[o] = b; result[o + 1] = g; result[o + 2] = r; result[o + 3] = (byte)Math.Round(alpha * 255);
                if (result[o + 3] > 0)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }
        }

        // A square around the logo, centred, with the margin on every side.
        int side = Math.Max(maxX - minX + 1, maxY - minY + 1);
        int pad = (int)Math.Round(side * margin / (1 - 2 * margin));
        int size = side + 2 * pad;
        int left = minX - (size - (maxX - minX + 1)) / 2, top = minY - (size - (maxY - minY + 1)) / 2;

        var cut = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var outData = cut.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var outPx = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int sx = left + x, sy = top + y;
                if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                Buffer.BlockCopy(result, (sy * w + sx) * 4, outPx, (y * size + x) * 4, 4);
            }
        }
        Marshal.Copy(outPx, 0, outData.Scan0, outPx.Length);
        cut.UnlockBits(outData);
        return cut;
    }

    // (C# 5: Windows PowerShell 5.1 compiles this.)
    static byte Unblend(byte c, double a)
    {
        return (byte)Math.Max(0, Math.Min(255, Math.Round((c - 255 * (1 - a)) / a)));
    }
}
'@
}

$sourceImage = New-Object System.Drawing.Bitmap (Resolve-Path $Source).Path
try {
    $logo = [FileFlussIconCutter]::Cut($sourceImage, $Margin)
}
finally {
    $sourceImage.Dispose()
}

# Every size Windows asks for (taskbar 24/32 at 100–200 %, title bar 16/20, Explorer up to 256).
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.DrawImage($logo, 0, 0, $size, $size)
    $graphics.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}
$logo.Dispose()

# ICO container with PNG-compressed entries.
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path (Resolve-Path (Split-Path $Output)).Path (Split-Path $Output -Leaf)), $file.ToArray())
$writer.Dispose()
"Wrote $Output ($($sizes -join ', ') px)"
