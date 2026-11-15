using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaForge.Integrity;

public static class IntegrityExport
{
    public static void WriteJson(string path, IntegrityReport report) => File.WriteAllText(path,
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }), new UTF8Encoding(false));

    public static void WriteCsv(string path, IEnumerable<IntegrityResult> results)
    {
        static string Cell(string value)
        {
            // Prevent spreadsheet formulas in filenames and decoder messages.
            if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Path,Status,Check,Decoder,CPU retry,Elapsed seconds,Summary,Diagnostics");
        foreach (var row in results) writer.WriteLine(string.Join(',', new[] { row.Path, row.StatusText, row.CheckText, row.Decoder, row.CpuRetry.ToString(), row.ElapsedSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), row.Summary, row.Diagnostics }.Select(Cell)));
    }
}
