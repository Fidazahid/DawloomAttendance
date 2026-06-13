using System.Collections.Generic;
using ClosedXML.Excel;

namespace DawloomAttendance.Services
{
    /// <summary>Writes a simple header + rows worksheet to an .xlsx file via ClosedXML.</summary>
    public static class ExcelExport
    {
        public static void Write(string path, string sheetName, IList<string> headers, IEnumerable<IList<string>> rows)
        {
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add(string.IsNullOrWhiteSpace(sheetName) ? "Report" : sheetName);

                for (int c = 0; c < headers.Count; c++)
                    ws.Cell(1, c + 1).Value = headers[c];
                ws.Row(1).Style.Font.Bold = true;
                ws.SheetView.FreezeRows(1);

                int r = 2;
                foreach (var row in rows)
                {
                    for (int c = 0; c < row.Count; c++)
                        ws.Cell(r, c + 1).Value = row[c];
                    r++;
                }

                ws.Columns().AdjustToContents();
                wb.SaveAs(path);
            }
        }
    }
}
