using System;
using System.IO;
using System.Text.RegularExpressions;

// Test file count parsing
string logPath = "../test-robocopy-log.txt";
if (!File.Exists(logPath))
{
    Console.WriteLine("Test log file not found!");
    return;
}

string content = File.ReadAllText(logPath);

// Test Files count parsing
var filePatterns = new[]
{
    @"Files\s*:\s*(\d+)\s+(\d+)",           // English: Files :   150    25
    @"Файлы\s*:\s*(\d+)\s+(\d+)",           // Russian: Файлы :   150    25
    @"Файлов\s*:\s*(\d+)\s+(\d+)"           // Russian alternative
};

Console.WriteLine("Testing file count parsing:");
bool foundFiles = false;
foreach (var pattern in filePatterns)
{
    var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
    if (match.Success && match.Groups.Count >= 3)
    {
        Console.WriteLine($"  Pattern: {pattern}");
        Console.WriteLine($"  Total: {match.Groups[1].Value}");
        Console.WriteLine($"  Copied: {match.Groups[2].Value}");
        Console.WriteLine($"  ✓ Successfully parsed!");
        foundFiles = true;
        break;
    }
}

if (!foundFiles)
{
    Console.WriteLine("  ✗ Failed to parse file count!");
}

// Test Bytes parsing
Console.WriteLine("\nTesting bytes parsing:");
var bytesPatterns = new[]
{
    @"Bytes\s*:\s*[\d\s,\.]+\s*[kmgtKMGT]?\s+([\d\s,\.]+)\s*([kmgtKMGT])?",  // English
    @"Байт[а-я]*\s*:\s*[\d\s,\.]+\s*[кмгтКМГТ]?\s+([\d\s,\.]+)\s*([кмгтКМГТ])?"  // Russian
};

bool foundBytes = false;
foreach (var pattern in bytesPatterns)
{
    var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
    if (match.Success && match.Groups.Count >= 2)
    {
        var numberStr = match.Groups[1].Value.Replace(",", "").Replace(" ", "").Trim();
        var unit = match.Groups.Count > 2 ? match.Groups[2].Value.ToLowerInvariant() : "";
        
        Console.WriteLine($"  Pattern matched!");
        Console.WriteLine($"  Extracted number: '{match.Groups[1].Value}'");
        Console.WriteLine($"  Unit: '{unit}'");
        Console.WriteLine($"  Cleaned number: '{numberStr}'");
        
        if (double.TryParse(numberStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            // Convert based on unit suffix
            long bytes = unit switch
            {
                "k" or "к" => (long)(number * 1024),
                "m" or "м" => (long)(number * 1024 * 1024),
                "g" or "г" => (long)(number * 1024 * 1024 * 1024),
                "t" or "т" => (long)(number * 1024L * 1024 * 1024 * 1024),
                _ => (long)number
            };
            
            Console.WriteLine($"  Parsed value: {bytes:N0} bytes");
            Console.WriteLine($"  In GB: {bytes / 1024.0 / 1024.0 / 1024.0:F2} GB");
            Console.WriteLine($"  ✓ Successfully parsed!");
        }
        else
        {
            Console.WriteLine($"  ✗ Failed to convert to number!");
        }
        foundBytes = true;
        break;
    }
}

if (!foundBytes)
{
    Console.WriteLine("  ✗ Failed to parse bytes!");
}

Console.WriteLine("\n=== Expected results ===");
Console.WriteLine("  Files copied: 25");
Console.WriteLine("  Bytes copied: 5,584,691,200 bytes (5.2 GB)");
