using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClosedXML.Excel;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Services
{
    /// <summary>Outcome of a bulk import: counts plus any per-row warnings/errors.</summary>
    public class ImportResult
    {
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public List<string> Errors { get; } = new List<string>();

        public override string ToString() =>
            $"{Added} added, {Updated} updated, {Skipped} skipped" +
            (Errors.Count > 0 ? $", {Errors.Count} warning(s)/error(s)" : "");
    }

    /// <summary>
    /// Bulk-imports employees from an .xlsx workbook: the first row is headers (matched
    /// case-insensitively against a set of aliases), each following row is one employee.
    /// Rows are upserted by Enroll # — a matching employee is updated, otherwise added.
    /// </summary>
    public static class EmployeeImport
    {
        // Header text (lower-cased) → canonical field name.
        private static readonly Dictionary<string, string> HeaderMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enroll"] = "enroll", ["enroll #"] = "enroll", ["enroll#"] = "enroll",
                ["enrollnumber"] = "enroll", ["enroll number"] = "enroll", ["id"] = "enroll", ["user id"] = "enroll",
                ["name"] = "name",
                ["cnic"] = "cnic",
                ["department"] = "department", ["dept"] = "department",
                ["designation"] = "designation", ["title"] = "designation",
                ["contact"] = "contact", ["phone"] = "contact", ["mobile"] = "contact",
                ["email"] = "email", ["e-mail"] = "email",
                ["salary"] = "salary",
                ["shift"] = "shift",
                ["active"] = "active",
            };

        /// <summary>The columns written by <see cref="WriteTemplate"/> (also the recommended layout).</summary>
        private static readonly string[] TemplateHeaders =
            { "Enroll #", "Name", "CNIC", "Department", "Designation", "Contact", "Email", "Salary", "Shift", "Active" };

        public static ImportResult FromExcel(string path, AppDb db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var result = new ImportResult();

            var shiftsByName = db.GetShifts()
                .GroupBy(s => (s.Name ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            var existingByEnroll = db.GetEmployees()
                .GroupBy(e => e.EnrollNumber, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            using (var wb = new XLWorkbook(path))
            {
                var ws = wb.Worksheets.First();
                var rows = ws.RangeUsed()?.RowsUsed().ToList();
                if (rows == null || rows.Count < 2)
                {
                    result.Errors.Add("No data rows found (need a header row plus at least one employee).");
                    return result;
                }

                // Map each column number to a canonical field via the header row.
                var colToField = new Dictionary<int, string>();
                foreach (var cell in rows[0].Cells())
                {
                    var key = (cell.GetString() ?? "").Trim();
                    if (HeaderMap.TryGetValue(key, out var field))
                        colToField[cell.Address.ColumnNumber] = field;
                }
                if (!colToField.Values.Contains("enroll"))
                {
                    result.Errors.Add("No 'Enroll #' column found in the header row.");
                    return result;
                }

                for (int i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var vals = new Dictionary<string, string>();
                    foreach (var kv in colToField)
                        vals[kv.Value] = row.Cell(kv.Key).GetString()?.Trim();

                    vals.TryGetValue("enroll", out var enroll);
                    if (string.IsNullOrWhiteSpace(enroll)) { result.Skipped++; continue; }

                    existingByEnroll.TryGetValue(enroll, out var emp);
                    bool isNew = emp == null;
                    if (isNew) emp = new Employee { EnrollNumber = enroll, Active = true };

                    if (vals.TryGetValue("name", out var v)) emp.Name = NullIfEmpty(v);
                    if (vals.TryGetValue("cnic", out v)) emp.Cnic = NullIfEmpty(v);
                    if (vals.TryGetValue("department", out v)) emp.Department = NullIfEmpty(v);
                    if (vals.TryGetValue("designation", out v)) emp.Designation = NullIfEmpty(v);
                    if (vals.TryGetValue("contact", out v)) emp.Contact = NullIfEmpty(v);
                    if (vals.TryGetValue("email", out v)) emp.Email = NullIfEmpty(v);

                    if (vals.TryGetValue("salary", out var salStr) && !string.IsNullOrWhiteSpace(salStr))
                    {
                        if (TryParseNumber(salStr, out var sal)) emp.Salary = sal;
                        else result.Errors.Add($"Row {row.RowNumber()}: salary '{salStr}' is not a number — left unchanged.");
                    }
                    if (vals.TryGetValue("active", out var actStr) && !string.IsNullOrWhiteSpace(actStr))
                        emp.Active = ParseBool(actStr);
                    if (vals.TryGetValue("shift", out var shiftName) && !string.IsNullOrWhiteSpace(shiftName))
                    {
                        if (shiftsByName.TryGetValue(shiftName.Trim(), out var sid)) emp.ShiftId = sid;
                        else result.Errors.Add($"Row {row.RowNumber()}: shift '{shiftName}' not found — left unassigned.");
                    }

                    try
                    {
                        if (isNew) { emp.Id = db.InsertEmployee(emp); existingByEnroll[enroll] = emp; result.Added++; }
                        else { db.UpdateEmployee(emp); result.Updated++; }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Row {row.RowNumber()} (enroll {enroll}): {ex.Message}");
                    }
                }
            }
            return result;
        }

        /// <summary>Writes a blank import template (headers + one example row) to <paramref name="path"/>.</summary>
        public static void WriteTemplate(string path)
        {
            var example = new List<IList<string>>
            {
                new[] { "1001", "Ali Khan", "35202-1234567-1", "Production", "Operator", "0300-1234567", "ali@example.com", "50000", "Day", "Yes" }
            };
            ExcelExport.Write(path, "Employees", TemplateHeaders, example);
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static bool TryParseNumber(string s, out double value) =>
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out value);

        private static bool ParseBool(string s)
        {
            s = s.Trim().ToLowerInvariant();
            return !(s == "no" || s == "false" || s == "0" || s == "inactive" || s == "n");
        }
    }
}
