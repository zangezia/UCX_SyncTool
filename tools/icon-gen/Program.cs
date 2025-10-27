using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

// Simple ICO writer: embed PNG-encoded images for sizes.
// Usage: IconGen.exe <inputImagePath> <outputIcoPath>

string? inPath = args.Length > 0 ? args[0] : "..\\..\\..\\logo.jpg";
string? outPath = args.Length > 1 ? args[1] : "..\\..\\..\\UCXSyncTool\\Assets\\app_icon.ico";

if (!File.Exists(inPath))
{
    Console.Error.WriteLine($"Input file not found: {inPath}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

using var original = Image.FromFile(inPath);
int[] sizes = new[] {256, 48, 32, 16};

var pngData = sizes.Select(s =>
{
    using var bmp = new Bitmap(s, s);
    using var g = Graphics.FromImage(bmp);
    g.Clear(Color.Transparent);
    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    g.DrawImage(original, 0, 0, s, s);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}).ToArray();

using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
using var bw = new BinaryWriter(outFs);

// ICONDIR
bw.Write((short)0); // reserved
bw.Write((short)1); // type = 1 icon
bw.Write((short)sizes.Length); // count

int offset = 6 + 16 * sizes.Length; // header + entries
for (int i = 0; i < sizes.Length; i++)
{
    int s = sizes[i];
    var data = pngData[i];
    // ICONDIRENTRY
    bw.Write((byte)(s >= 256 ? 0 : s)); // width
    bw.Write((byte)(s >= 256 ? 0 : s)); // height
    bw.Write((byte)0); // color palette
    bw.Write((byte)0); // reserved
    bw.Write((short)0); // color planes
    bw.Write((short)32); // bits per pixel
    bw.Write(data.Length); // size of image data
    bw.Write(offset); // offset of image data
    offset += data.Length;
}

// write image data
for (int i = 0; i < sizes.Length; i++)
{
    bw.Write(pngData[i]);
}

Console.WriteLine($"Wrote icon to {outPath}");
return 0;