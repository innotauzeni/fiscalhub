using System.Text;
using UglyToad.PdfPig;

namespace Fdms.Infrastructure.Import
{
    /// <summary>Reads the text layer of a digital PDF and rebuilds visual lines (words sharing a baseline), which keeps table rows intact.</summary>
    public static class PdfTextExtractor
    {
        public const int MaxPages = 30;

        public static string Extract(Stream file)
        {
            using var ms = new MemoryStream();
            file.CopyTo(ms);
            using var doc = PdfDocument.Open(ms.ToArray());
            var sb = new StringBuilder();
            var pages = 0;
            foreach (var page in doc.GetPages())
            {
                if (++pages > MaxPages) break;
                var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                // group by baseline within 3pt, top of the page first
                var lines = new List<(double Y, List<UglyToad.PdfPig.Content.Word> Words)>();
                foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
                {
                    var y = w.BoundingBox.Bottom;
                    var line = lines.FirstOrDefault(l => Math.Abs(l.Y - y) <= 3);
                    if (line.Words == null) lines.Add((y, new List<UglyToad.PdfPig.Content.Word> { w }));
                    else line.Words.Add(w);
                }
                foreach (var l in lines)
                    sb.AppendLine(string.Join(" ", l.Words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            }
            return sb.ToString();
        }
    }
}
