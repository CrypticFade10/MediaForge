using System.Globalization;
using System.Text;

namespace SimilarVideoFinder.Core;

public static class ReportExporter
{
    public static void WriteCsv(string path, ScanReport report)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Group,Match type,Path,Size bytes,Score,Reason,Duration seconds,Width,Height,Video codec,Audio codec,Protected reference,Bit depth,Dynamic range,Media tracks");
        static string Quote(string value)
        {
            // Avoid executing spreadsheet formulas when reports are opened in Excel.
            if (value.Length > 0 && ("=+-@\t\r".Contains(value[0]))) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        foreach (var group in report.Groups) foreach (var member in group.Members)
        {
            var f = member.File; var m = f.Media;
            string[] cells = [group.Id, group.Label, f.Path, f.Size.ToString(CultureInfo.InvariantCulture),
                member.Evidence.Score.ToString("0.00", CultureInfo.InvariantCulture), member.Evidence.Reason,
                m?.Duration.ToString("0.###", CultureInfo.InvariantCulture) ?? "", m?.Width.ToString() ?? "", m?.Height.ToString() ?? "", m?.VideoCodec ?? "", m?.AudioCodec ?? "",
                ReferencePolicy.Protected(report, f) ? "Yes" : "No", m?.BitDepth.ToString(CultureInfo.InvariantCulture) ?? "", m?.DynamicRange ?? "", m?.Description ?? ""];
            writer.WriteLine(string.Join(',', cells.Select(Quote)));
        }
    }
}
