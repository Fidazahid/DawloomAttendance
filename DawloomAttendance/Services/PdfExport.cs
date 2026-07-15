using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Renders branded, professional PDFs (report tables, per-employee reports, salary
    /// slips) via MigraDoc — each with a Dawloom letterhead, brand colours and footer.
    /// </summary>
    public static class PdfExport
    {
        // ---- Brand ------------------------------------------------------------------
        // Sampled from the official Dawloom logo: deep indigo + periwinkle purple.
        internal static readonly Color BrandDark   = new Color(48, 38, 100);   // #302664
        internal static readonly Color BrandAccent = new Color(96, 88, 163);   // #6058A3
        internal static readonly Color BrandTint   = new Color(242, 241, 249);  // very light periwinkle (alt rows)
        internal static readonly Color RuleGray    = new Color(214, 214, 222);
        internal static readonly Color LabelGray   = new Color(120, 120, 130);

        private const string CompanyName = "Dawloom Software House";
        private const string CompanyTag  = "Attendance & Payroll System";

        // ---- Public API -------------------------------------------------------------

        /// <summary>A titled, striped report table (landscape A4) with the Dawloom letterhead.</summary>
        public static void Write(string path, string title, string subtitle, IList<string> headers, IList<IList<string>> rows)
        {
            var doc = NewDoc(Orientation.Landscape);
            var section = doc.AddSection();
            AddFooter(section);

            AddLetterhead(section, ContentWidthCm(doc), title, subtitle);
            RenderTable(section, headers, rows);

            Render(doc, path);
        }

        /// <summary>One employee's attendance report: letterhead, summary block, daily table.</summary>
        public static void WriteEmployeeReport(string path, string name, string enroll, string period,
            IList<KeyValuePair<string, string>> summary, IList<string> dailyHeaders, IList<IList<string>> dailyRows)
        {
            var doc = NewDoc(Orientation.Portrait);
            var section = doc.AddSection();
            AddFooter(section);

            AddLetterhead(section, ContentWidthCm(doc), "Attendance Report",
                $"{name}    •    Enroll #{enroll}    •    {period}");

            AddGroup(section, "Summary", summary);

            var dh = section.AddParagraph("Daily detail");
            dh.Format.Font.Bold = true; dh.Format.Font.Size = 11; dh.Format.Font.Color = BrandDark;
            dh.Format.SpaceBefore = Unit.FromCentimeter(0.4); dh.Format.SpaceAfter = Unit.FromCentimeter(0.12);

            RenderTable(section, dailyHeaders, dailyRows);

            Render(doc, path);
        }

        /// <summary>Renders one salary-slip page per employee.</summary>
        public static void WriteSalarySlips(string path, string period, IEnumerable<SalarySlip> slips)
        {
            var doc = NewDoc(Orientation.Portrait);

            foreach (var s in slips)
            {
                var section = doc.AddSection();   // new page per employee
                AddFooter(section);
                AddLetterhead(section, ContentWidthCm(doc), "Salary Slip", period);

                AddGroup(section, "Employee", new[]
                {
                    Pair("Name", s.Name), Pair("Enroll #", s.Enroll), Pair("CNIC", s.Cnic),
                    Pair("Department", s.Department), Pair("Designation", s.Designation), Pair("Shift", s.Shift),
                });
                AddGroup(section, "Attendance", new[]
                {
                    Pair("Working days", s.WorkingDays.ToString()),
                    Pair("Present", s.Present.ToString()),
                    Pair("Absent", s.Absent.ToString()),
                    Pair("Paid leave", s.PaidLeaveDays.ToString()),
                    Pair("Unpaid leave", s.UnpaidLeaveDays.ToString()),
                    Pair("Late count", s.LateCount.ToString()),
                    Pair("Late time", DurationFormat.Minutes(s.LateMinutes)),
                    Pair("Worked", DurationFormat.Hours(s.WorkedHours)),
                    Pair("Overtime", DurationFormat.Hours(s.OvertimeHours)),
                    Pair("Late deduction (days)", s.LateDeductionDays.ToString("0.##")),
                    Pair("Payable days", s.PayableDays.ToString("0.##")),
                });
                AddGroup(section, "Earnings (Rs.)", new[]
                {
                    Pair("Monthly salary", s.Salary.ToString("0")),
                    Pair("Daily rate", s.DailyRate.ToString("0")),
                    Pair("Base pay (payable days)", s.BasePay.ToString("0")),
                    Pair("Overtime pay" + (s.IncludeOvertime ? "" : " (excluded)"), s.OvertimePay.ToString("0")),
                });

                // Highlighted net-pay banner in the brand tint.
                var net = section.AddParagraph();
                net.Format.SpaceBefore = Unit.FromCentimeter(0.35);
                net.Format.Shading.Color = BrandTint;
                net.Format.Borders.Left = new Border { Width = 3, Color = BrandAccent };
                net.Format.Borders.Color = RuleGray;
                net.Format.Borders.Width = 0.25;
                net.Format.LeftIndent = Unit.FromCentimeter(0.25);
                net.Format.SpaceAfter = Unit.FromCentimeter(0.12);
                var pad = net.Format.Borders; // padding via distance
                pad.DistanceFromTop = Unit.FromCentimeter(0.15);
                pad.DistanceFromBottom = Unit.FromCentimeter(0.15);
                net.AddFormattedText("Net pay:   Rs. " + s.NetPay.ToString("0"),
                    new Font { Size = 15, Bold = true, Color = BrandDark });
            }

            Render(doc, path);
        }

        // ---- Layout building blocks -------------------------------------------------

        private static Document NewDoc(Orientation orientation)
        {
            var doc = new Document();
            doc.DefaultPageSetup.Orientation = orientation;
            doc.DefaultPageSetup.PageFormat = PageFormat.A4;
            doc.DefaultPageSetup.TopMargin = Unit.FromCentimeter(1.4);
            doc.DefaultPageSetup.BottomMargin = Unit.FromCentimeter(1.4);
            doc.DefaultPageSetup.LeftMargin = Unit.FromCentimeter(1.6);
            doc.DefaultPageSetup.RightMargin = Unit.FromCentimeter(1.6);
            doc.Styles["Normal"].Font.Name = "Segoe UI";
            doc.Styles["Normal"].Font.Size = 9.5;
            return doc;
        }

        private static double ContentWidthCm(Document doc)
        {
            double pageW = doc.DefaultPageSetup.Orientation == Orientation.Landscape ? 29.7 : 21.0;
            return pageW
                - doc.DefaultPageSetup.LeftMargin.Centimeter
                - doc.DefaultPageSetup.RightMargin.Centimeter;
        }

        /// <summary>Logo (left) + company name (right), an accent rule, then the document title.</summary>
        private static void AddLetterhead(Section section, double contentWidthCm, string docTitle, string subtitle)
        {
            var band = section.AddTable();
            band.Borders.Width = 0;
            const double logoColCm = 8.5;
            band.AddColumn(Unit.FromCentimeter(logoColCm));
            band.AddColumn(Unit.FromCentimeter(Math.Max(4.0, contentWidthCm - logoColCm)));

            var row = band.AddRow();
            row.VerticalAlignment = VerticalAlignment.Center;

            string logo = LogoPath();
            if (logo != null)
            {
                var img = row.Cells[0].AddParagraph().AddImage(logo);
                img.Width = Unit.FromCentimeter(4.9);
                img.LockAspectRatio = true;
            }
            else
            {
                var wm = row.Cells[0].AddParagraph("dawloom");
                wm.Format.Font.Size = 22; wm.Format.Font.Bold = true; wm.Format.Font.Color = BrandAccent;
            }

            var nameCell = row.Cells[1];
            var nm = nameCell.AddParagraph(CompanyName);
            nm.Format.Alignment = ParagraphAlignment.Right;
            nm.Format.Font.Size = 13; nm.Format.Font.Bold = true; nm.Format.Font.Color = BrandDark;
            var tg = nameCell.AddParagraph(CompanyTag);
            tg.Format.Alignment = ParagraphAlignment.Right;
            tg.Format.Font.Size = 8.5; tg.Format.Font.Color = BrandAccent;

            var rule = section.AddParagraph();
            rule.Format.SpaceBefore = Unit.FromCentimeter(0.12);
            rule.Format.Borders.Bottom = new Border { Width = 1.6, Color = BrandAccent };
            rule.Format.SpaceAfter = Unit.FromCentimeter(0.32);

            var title = section.AddParagraph(docTitle);
            title.Format.Font.Size = 17; title.Format.Font.Bold = true; title.Format.Font.Color = BrandDark;

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = section.AddParagraph(subtitle);
                sub.Format.Font.Size = 9.5; sub.Format.Font.Color = LabelGray;
                sub.Format.SpaceAfter = Unit.FromCentimeter(0.35);
            }
            else title.Format.SpaceAfter = Unit.FromCentimeter(0.35);
        }

        private static void AddFooter(Section section)
        {
            var footer = section.Footers.Primary.AddParagraph();
            footer.Format.Borders.Top = new Border { Width = 0.5, Color = RuleGray };
            footer.Format.Borders.DistanceFromBottom = Unit.FromCentimeter(0.1);
            footer.AddText(CompanyName + "  •  " + CompanyTag + "      Generated " +
                           DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "      Page ");
            footer.AddPageField();
            footer.Format.Font.Size = 8; footer.Format.Font.Color = LabelGray;
        }

        /// <summary>Styled header + striped rows table (header repeats on page breaks).</summary>
        private static void RenderTable(Section section, IList<string> headers, IList<IList<string>> rows)
        {
            var table = section.AddTable();
            table.Borders.Width = 0.25;
            table.Borders.Color = new Color(225, 225, 232);

            for (int c = 0; c < headers.Count; c++)
            {
                int maxLen = headers[c]?.Length ?? 0;
                foreach (var row in rows)
                    if (c < row.Count) maxLen = Math.Max(maxLen, (row[c] ?? "").Length);
                double cm = Math.Min(7.0, Math.Max(1.6, maxLen * 0.22));
                table.AddColumn(Unit.FromCentimeter(cm));
            }

            var head = table.AddRow();
            head.HeadingFormat = true;            // repeat the header row on every page
            head.Height = Unit.FromCentimeter(0.72);
            head.Shading.Color = BrandDark;
            head.Format.Font.Bold = true; head.Format.Font.Color = Colors.White;
            head.VerticalAlignment = VerticalAlignment.Center;
            for (int c = 0; c < headers.Count; c++)
            {
                var p = head.Cells[c].AddParagraph(headers[c] ?? "");
                p.Format.LeftIndent = Unit.FromCentimeter(0.1);
            }

            int i = 0;
            foreach (var row in rows)
            {
                var r = table.AddRow();
                r.Height = Unit.FromCentimeter(0.55);
                r.VerticalAlignment = VerticalAlignment.Center;
                if (i++ % 2 == 1) r.Shading.Color = BrandTint;
                for (int c = 0; c < headers.Count; c++)
                {
                    var p = r.Cells[c].AddParagraph(c < row.Count ? (row[c] ?? "") : "");
                    p.Format.LeftIndent = Unit.FromCentimeter(0.1);
                }
            }
        }

        private static KeyValuePair<string, string> Pair(string k, string v)
            => new KeyValuePair<string, string>(k, v ?? "");

        private static void AddGroup(Section section, string heading, IEnumerable<KeyValuePair<string, string>> rows)
        {
            var h = section.AddParagraph(heading);
            h.Format.Font.Bold = true; h.Format.Font.Size = 11; h.Format.Font.Color = BrandDark;
            h.Format.SpaceBefore = Unit.FromCentimeter(0.3);
            h.Format.SpaceAfter = Unit.FromCentimeter(0.12);
            h.Format.Borders.Bottom = new Border { Width = 0.5, Color = RuleGray };

            var table = section.AddTable();
            table.Borders.Width = 0;
            table.AddColumn(Unit.FromCentimeter(6));
            table.AddColumn(Unit.FromCentimeter(9));
            foreach (var kv in rows)
            {
                var r = table.AddRow();
                var l = r.Cells[0].AddParagraph(kv.Key);
                l.Format.Font.Color = LabelGray;
                var val = r.Cells[1].AddParagraph(kv.Value ?? "");
                val.Format.Font.Bold = true;
            }
        }

        private static void Render(Document doc, string path)
        {
            var renderer = new PdfDocumentRenderer(true) { Document = doc };
            renderer.RenderDocument();
            renderer.PdfDocument.Save(path);
        }

        // ---- Embedded logo ----------------------------------------------------------

        private static string _logoPath;

        /// <summary>
        /// Extracts the embedded Dawloom wordmark to a stable temp file (once per process)
        /// and returns its path for MigraDoc to draw. Returns null if the resource is missing
        /// (the letterhead then falls back to a text wordmark).
        /// </summary>
        private static string LogoPath()
        {
            if (_logoPath != null && File.Exists(_logoPath)) return _logoPath;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("DawloomLogo.png", StringComparison.OrdinalIgnoreCase));
                if (res == null) return null;

                var path = Path.Combine(Path.GetTempPath(), "Dawloom_Letterhead_Logo.png");
                using (var s = asm.GetManifestResourceStream(res))
                using (var fs = File.Create(path))
                    s.CopyTo(fs);
                _logoPath = path;
                return _logoPath;
            }
            catch { return null; }
        }
    }
}
