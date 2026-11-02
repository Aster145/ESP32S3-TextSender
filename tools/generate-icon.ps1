param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets')
)

# Rebuild the application icon with Windows' built-in System.Drawing.
# No fonts, downloaded artwork, or third-party packages are required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('TextSenderIconGenerator' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class TextSenderIconGenerator
{
    private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        GraphicsPath path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillRounded(Graphics g, Brush brush, float x, float y,
                                    float w, float h, float r)
    {
        using (GraphicsPath shape = Rounded(x, y, w, h, r))
            g.FillPath(brush, shape);
    }

    private static Bitmap Render(int size)
    {
        // Supersample the vector shapes so the small taskbar sizes stay legible.
        using (Bitmap large = new Bitmap(size * 4, size * 4, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(large))
            using (Brush white = new SolidBrush(Color.White))
            using (Brush keyColor = new SolidBrush(Color.FromArgb(13, 133, 155)))
            using (LinearGradientBrush background = new LinearGradientBrush(
                new PointF(16, 16), new PointF(240, 240),
                Color.FromArgb(20, 184, 166), Color.FromArgb(15, 112, 171)))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size * 4 / 256f, size * 4 / 256f);
                FillRounded(g, background, 8, 8, 240, 240, 52);

                // The separate arrow reads as sending without obscuring the keyboard.
                using (Pen arrow = new Pen(Color.White, 16))
                {
                    arrow.StartCap = LineCap.Round;
                    arrow.EndCap = LineCap.Round;
                    arrow.LineJoin = LineJoin.Round;
                    g.DrawLine(arrow, 103, 65, 204, 65);
                    g.DrawLines(arrow, new PointF[] {
                        new PointF(182, 43), new PointF(204, 65), new PointF(182, 87)
                    });
                }

                FillRounded(g, white, 40, 103, 176, 103, 16);
                int columns = size <= 24 ? 5 : 6;
                float width = size <= 24 ? 21 : 17;
                float gap = 9;
                float start = (256 - (columns * width + (columns - 1) * gap)) / 2;
                for (int row = 0; row < 2; ++row)
                    for (int column = 0; column < columns; ++column)
                        FillRounded(g, keyColor, start + column * (width + gap),
                                    118 + row * 26, width, 16, 3);
                FillRounded(g, keyColor, 81, 176, 94, 14, 4);
            }

            Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(large, new Rectangle(0, 0, size, size));
            }
            return result;
        }
    }

    private static byte[] IconFrame(Bitmap bitmap)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            // Use 32-bit DIB frames, including 256px, for System.Drawing.Icon compatibility.
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int size = bitmap.Width;
                int maskStride = ((size + 31) / 32) * 4;
                writer.Write(40);              // BITMAPINFOHEADER size
                writer.Write(size);
                writer.Write(size * 2);        // XOR image plus AND mask
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(0);               // BI_RGB
                writer.Write(size * size * 4 + maskStride * size);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                for (int y = size - 1; y >= 0; --y)
                    for (int x = 0; x < size; ++x)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        writer.Write(pixel.B); writer.Write(pixel.G);
                        writer.Write(pixel.R); writer.Write(pixel.A);
                    }
                for (int y = size - 1; y >= 0; --y)
                {
                    byte[] mask = new byte[maskStride];
                    for (int x = 0; x < size; ++x)
                        if (bitmap.GetPixel(x, y).A == 0)
                            mask[x / 8] |= (byte)(0x80 >> (x % 8));
                    writer.Write(mask);
                }
                return stream.ToArray();
            }
        }
    }

    public static void Generate(string directory)
    {
        Directory.CreateDirectory(directory);
        int[] sizes = {16, 24, 32, 48, 64, 128, 256};
        byte[][] frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; ++i)
            using (Bitmap image = Render(sizes[i]))
            {
                frames[i] = IconFrame(image);
                if (sizes[i] == 256)
                    image.Save(Path.Combine(directory, "TextSender-preview.png"), ImageFormat.Png);
            }
        using (BinaryWriter writer = new BinaryWriter(File.Create(Path.Combine(directory, "TextSender.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; ++i)
            {
                byte dimension = sizes[i] == 256 ? (byte)0 : (byte)sizes[i];
                writer.Write(dimension); writer.Write(dimension);
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
    }
}
'@
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[TextSenderIconGenerator]::Generate($resolvedOutput)
$iconPath = Join-Path $resolvedOutput 'TextSender.ico'
$combinedIcon = New-Object System.Drawing.Icon($iconPath)
$combinedIcon.Dispose()
$iconBytes = [System.IO.File]::ReadAllBytes($iconPath)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
for ($index = 0; $index -lt $sizes.Length; $index++) {
    # .NET Framework's multi-frame selector treats the 256px directory byte as 0.
    # Load each embedded frame separately to verify every size, including 256px.
    $size = $sizes[$index]
    $entry = 6 + $index * 16
    $frameLength = [BitConverter]::ToInt32($iconBytes, $entry + 8)
    $frameOffset = [BitConverter]::ToInt32($iconBytes, $entry + 12)
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $icon = $null
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]1)
        $writer.Write($iconBytes, $entry, 12)
        $writer.Write([int32]22)
        $writer.Write($iconBytes, $frameOffset, $frameLength)
        $writer.Flush()
        $stream.Position = 0
        $icon = New-Object System.Drawing.Icon($stream, $size, $size)
        if ($icon.Width -ne $size -or $icon.Height -ne $size) {
            throw "The $size pixel ICO frame could not be loaded."
        }
    } finally {
        if ($icon) { $icon.Dispose() }
        $writer.Dispose()
        $stream.Dispose()
    }
}
Write-Output "Generated and loaded all 7 icon sizes: $iconPath"
Write-Output (Join-Path $resolvedOutput 'TextSender-preview.png')
