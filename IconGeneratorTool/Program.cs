using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;

class Program
{
    static void Main()
    {
        // Размеры для иконки
        int[] sizes = { 16, 32, 48, 64, 128, 256 };

        Console.WriteLine("Загрузка logo.tif...");
        var logoPath = Path.Combine("..", "logo.tif");
        using var sourceImage = Image.FromFile(logoPath);
        
        // Создаем список bitmap для разных размеров
        var bitmaps = new List<Bitmap>();

        foreach (var size in sizes)
        {
            Console.WriteLine($"Создание изображения {size}x{size}...");
            var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(sourceImage, 0, 0, size, size);
            }
            bitmaps.Add(bitmap);
        }

        Console.WriteLine("Сохранение icon.ico...");
        
        // Убедимся, что папка Assets существует
        var outputDir = Path.Combine("..", "UCXSyncTool", "Assets");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, "icon.ico");
        
        // Сохраняем как ICO
        using (var fs = new FileStream(outputPath, FileMode.Create))
        {
            using (var writer = new BinaryWriter(fs))
            {
                // ICONDIR header
                writer.Write((short)0);              // Reserved
                writer.Write((short)1);              // Type (1 = ICO)
                writer.Write((short)bitmaps.Count);  // Number of images

                int offset = 6 + (16 * bitmaps.Count);
                var imageData = new List<byte[]>();

                // ICONDIRENTRY for each size
                foreach (var bitmap in bitmaps)
                {
                    using (var ms = new MemoryStream())
                    {
                        bitmap.Save(ms, ImageFormat.Png);
                        var data = ms.ToArray();
                        imageData.Add(data);

                        writer.Write((byte)(bitmap.Width == 256 ? 0 : bitmap.Width));    // Width (0 means 256)
                        writer.Write((byte)(bitmap.Height == 256 ? 0 : bitmap.Height));  // Height (0 means 256)
                        writer.Write((byte)0);               // Color palette
                        writer.Write((byte)0);               // Reserved
                        writer.Write((short)1);              // Color planes
                        writer.Write((short)32);             // Bits per pixel
                        writer.Write(data.Length);           // Size of image data
                        writer.Write(offset);                // Offset to image data
                        
                        offset += data.Length;
                    }
                }

                // Write image data
                foreach (var data in imageData)
                {
                    writer.Write(data);
                }
            }
        }

        // Очистка
        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }

        Console.WriteLine($"✓ Иконка успешно создана: {outputPath}");
    }
}
