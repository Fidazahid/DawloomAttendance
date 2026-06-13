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

        /// <summary>Renders one salary-slip page per employee.</summary>
        public static void WriteSalarySlips(string path, string period, IEnumerable<SalarySlip> slips)
        {
            var doc = new Document();
            doc.DefaultPageSetup.PageFormat = PageFormat.A4;
            doc.DefaultPageSetup.TopMargin = Unit.FromCentimeter(1.8);
            doc.DefaultPageSetup.LeftMargin = Unit.FromCentimeter(2);
            doc.DefaultPageSetup.RightMargin = Unit.FromCentimeter(2);

            var headerColor = new Color(45, 62, 80);

            foreach (var s in slips)
            {
                var section = doc.AddSection();   // new page per employee

                var title = section.AddParagraph("SALARY SLIP");
                title.Format.Alignment = ParagraphAlignment.Center;
                title.Format.Font.Size = 18; title.Format.Font.Bold = true; title.Format.Font.Color = headerColor;
                var sub = section.AddParagraph("Dawloom Attendance    •    " + period);
                sub.Format.Alignment = ParagraphAlignment.Center;
                sub.Format.Font.Size = 9; sub.Format.Font.Color = Colors.Gray;
                sub.Format.SpaceAfter = Unit.FromCentimeter(0.4);

                AddGroup(section, headerColor, "Employee", new[]
                {
                    Pair("Name", s.Name), Pair("Enroll #", s.Enroll), Pair("CNIC", s.Cnic),
                    Pair("Department", s.Department), Pair("Designation", s.Designation), Pair("Shift", s.Shift),
                });
                AddGroup(section, headerColor, "Attendance", new[]
                {
                    Pair("Working days", s.WorkingDays.ToString()),
                    Pair("Present", s.Present.ToString()),
                    Pair("Absent", s.Absent.ToString()),
                    Pair("Leave / Holiday", s.LeaveDays.ToString()),
                    Pair("Late count", s.LateCount.ToString()),
                    Pair("Late time", DurationFormat.Minutes(s.LateMinutes)),
                    Pair("Worked", DurationFormat.Hours(s.WorkedHours)),
                    Pair("Overtime", DurationFormat.Hours(s.OvertimeHours)),
                    Pair("Late deduction (days)", s.LateDeductionDays.ToString("0.##")),
                    Pair("Payable days", s.PayableDays.ToString("0.##")),
                });
                AddGroup(section, headerColor, "Earnings (Rs.)", new[]
                {
                    Pair("Monthly salary", s.Salary.ToString("0")),
                    Pair("Daily rate", s.DailyRate.ToString("0")),
                    Pair("Base pay (payable days)", s.BasePay.ToString("0")),
                    Pair("Overtime pay" + (s.IncludeOvertime ? "" : " (excluded)"), s.OvertimePay.ToString("0")),
                });

                var net = section.AddParagraph();
                net.Format.SpaceBefore = Unit.FromCentimeter(0.3);
                net.AddFormattedText("Net pay:   Rs. " + s.NetPay.ToString("0"),
                    new Font { Size = 15, Bold = true, Color = headerColor });
            }

            var renderer = new PdfDocumentRenderer(true) { Document = doc };
            renderer.RenderDocument();
            renderer.PdfDocument.Save(path);
        }

        private static KeyValuePair<string, string> Pair(string k, string v)
            => new KeyValuePair<string, string>(k, v ?? "");

        private static void AddGroup(Section section, Color headerColor, string heading, IEnumerable<KeyValuePair<string, string>> rows)
        {
            var h = section.AddParagraph(heading);
            h.Format.Font.Bold = true; h.Format.Font.Size = 11; h.Format.Font.Color = headerColor;
            h.Format.SpaceBefore = Unit.FromCentimeter(0.25);
            h.Format.SpaceAfter = Unit.FromCentimeter(0.1);

            var table = section.AddTable();
            table.Borders.Width = 0;
            table.AddColumn(Unit.FromCentimeter(6));
            table.AddColumn(Unit.FromCentimeter(9));
            foreach (var kv in rows)
            {
                var r = table.AddRow();
                var l = r.Cells[0].AddParagraph(kv.Key);
                l.Format.Font.Color = Colors.Gray;
                r.Cells[1].AddParagraph(kv.Value ?? "");
            }
        }
    }
}
