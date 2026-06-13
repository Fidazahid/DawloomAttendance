using System;
using System.Collections.Generic;
using System.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace DawloomAttendance.Services
{
    /// <summary>Renders a titled, styled table to a PDF (landscape A4) via MigraDoc.</summary>
    public static class PdfExport
    {
        public static void Write(string path, string title, string subtitle, IList<string> headers, IList<IList<string>> rows)
        {
            var doc = new Document();
            doc.DefaultPageSetup.Orientation = Orientation.Landscape;
            doc.DefaultPageSetup.PageFormat = PageFormat.A4;
            doc.DefaultPageSetup.TopMargin = Unit.FromCentimeter(1.6);
            doc.DefaultPageSetup.BottomMargin = Unit.FromCentimeter(1.4);
            doc.DefaultPageSetup.LeftMargin = Unit.FromCentimeter(1.4);
            doc.DefaultPageSetup.RightMargin = Unit.FromCentimeter(1.4);

            var section = doc.AddSection();

            var headerColor = new Color(45, 62, 80);     // dark slate
            var altRow = new Color(240, 244, 248);        // light blue-grey

            var titleP = section.AddParagraph(title);
            titleP.Format.Font.Size = 16;
            titleP.Format.Font.Bold = true;
            titleP.Format.Font.Color = headerColor;

            if (!string.IsNullOrEmpty(subtitle))
            {
                var subP = section.AddParagraph(subtitle);
                subP.Format.Font.Size = 9.5;
                subP.Format.Font.Color = Colors.Gray;
                subP.Format.SpaceAfter = Unit.FromCentimeter(0.4);
            }

            // Footer with generated timestamp + page number.
            var footer = section.Footers.Primary.AddParagraph();
            footer.AddText("Dawloom Attendance — generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "      Page ");
            footer.AddPageField();
            footer.Format.Font.Size = 8;
            footer.Format.Font.Color = Colors.Gray;

            var table = section.AddTable();
            table.Borders.Width = 0.25;
            table.Borders.Color = new Color(210, 210, 210);
            table.Rows.LeftIndent = 0;

            // Column widths sized from the longest cell in each column.
            for (int c = 0; c < headers.Count; c++)
            {
                int maxLen = headers[c]?.Length ?? 0;
                foreach (var row in rows)
                    if (c < row.Count) maxLen = Math.Max(maxLen, (row[c] ?? "").Length);
                double cm = Math.Min(7.0, Math.Max(1.6, maxLen * 0.22));
                table.AddColumn(Unit.FromCentimeter(cm));
            }

            var head = table.AddRow();
            head.Shading.Color = headerColor;
            head.Format.Font.Bold = true;
            head.Format.Font.Color = Colors.White;
            head.VerticalAlignment = VerticalAlignment.Center;
            for (int c = 0; c < headers.Count; c++)
                head.Cells[c].AddParagraph(headers[c] ?? "");

            int i = 0;
            foreach (var row in rows)
            {
                var r = table.AddRow();
                if (i++ % 2 == 1) r.Shading.Color = altRow;
                for (int c = 0; c < headers.Count; c++)
                    r.Cells[c].AddParagraph(c < row.Count ? (row[c] ?? "") : "");
            }

            var renderer = new PdfDocumentRenderer(true) { Document = doc };
            renderer.RenderDocument();
            renderer.PdfDocument.Save(path);
        }
    }
}
