using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DawloomAttendance.Tests
{
    /// <summary>
    /// Bulk Excel import against a real temp SQLite DB and real .xlsx files: add vs update
    /// (upsert by Enroll #), field parsing, shift resolution, and header validation.
    /// </summary>
    [TestClass]
    public class EmployeeImportTests
    {
        private string _dbPath;
        private AppDb _db;
        private readonly List<string> _temp = new List<string>();

        private static readonly string[] Header =
            { "Enroll #", "Name", "CNIC", "Department", "Designation", "Contact", "Salary", "Shift", "Active" };

        [TestInitialize]
        public void Init()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "dawloom_test_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new AppDb(_dbPath);
            _db.Initialize();
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { File.Delete(_dbPath); } catch { }
            foreach (var f in _temp) { try { File.Delete(f); } catch { } }
        }

        private string MakeXlsx(params string[][] rows)
        {
            var path = Path.Combine(Path.GetTempPath(), "imp_" + Guid.NewGuid().ToString("N") + ".xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Employees");
                for (int r = 0; r < rows.Length; r++)
                    for (int c = 0; c < rows[r].Length; c++)
                        ws.Cell(r + 1, c + 1).Value = rows[r][c];
                wb.SaveAs(path);
            }
            _temp.Add(path);
            return path;
        }

        private Employee Emp(string enroll) => _db.GetEmployees().Single(e => e.EnrollNumber == enroll);

        [TestMethod]
        public void Import_AddsNewEmployees_WithFields()
        {
            var path = MakeXlsx(
                Header,
                new[] { "1001", "Ali Khan", "35202-1", "Production", "Operator", "0300", "50000", "", "Yes" },
                new[] { "1002", "Sara", "", "QA", "Tester", "", "30000", "", "Yes" });

            var result = EmployeeImport.FromExcel(path, _db);

            Assert.AreEqual(2, result.Added);
            Assert.AreEqual(0, result.Updated);
            Assert.AreEqual(2, _db.GetEmployees().Count);

            var ali = Emp("1001");
            Assert.AreEqual("Ali Khan", ali.Name);
            Assert.AreEqual("Production", ali.Department);
            Assert.AreEqual(50000, ali.Salary, 0.001);
        }

        [TestMethod]
        public void Reimport_UpdatesExisting_ByEnroll()
        {
            var first = MakeXlsx(Header, new[] { "1001", "Ali", "", "", "", "", "50000", "", "Yes" });
            EmployeeImport.FromExcel(first, _db);

            var second = MakeXlsx(Header, new[] { "1001", "Ali Khan", "", "", "", "", "55000", "", "Yes" });
            var result = EmployeeImport.FromExcel(second, _db);

            Assert.AreEqual(0, result.Added);
            Assert.AreEqual(1, result.Updated);
            Assert.AreEqual(1, _db.GetEmployees().Count, "upsert must not create a duplicate");
            Assert.AreEqual("Ali Khan", Emp("1001").Name);
            Assert.AreEqual(55000, Emp("1001").Salary, 0.001);
        }

        [TestMethod]
        public void BlankEnrollRow_IsSkipped()
        {
            var path = MakeXlsx(
                Header,
                new[] { "", "No Enroll", "", "", "", "", "", "", "Yes" },
                new[] { "1003", "Has Enroll", "", "", "", "", "", "", "Yes" });

            var result = EmployeeImport.FromExcel(path, _db);

            Assert.AreEqual(1, result.Added);
            Assert.AreEqual(1, result.Skipped);
        }

        [TestMethod]
        public void MissingEnrollColumn_ReportsError_AddsNothing()
        {
            var path = MakeXlsx(
                new[] { "Name", "Salary" },
                new[] { "Ali", "50000" });

            var result = EmployeeImport.FromExcel(path, _db);

            Assert.AreEqual(0, result.Added);
            Assert.IsTrue(result.Errors.Any(e => e.IndexOf("Enroll", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public void Shift_ResolvedByName_UnknownShiftWarns()
        {
            long dayId = _db.InsertShift(new Shift { Name = "Day", StartTime = "09:00", EndTime = "18:00", Active = true });

            var ok = MakeXlsx(Header, new[] { "1001", "Ali", "", "", "", "", "", "Day", "Yes" });
            EmployeeImport.FromExcel(ok, _db);
            Assert.AreEqual(dayId, Emp("1001").ShiftId);

            var bad = MakeXlsx(Header, new[] { "1002", "Sara", "", "", "", "", "", "Night", "Yes" });
            var result = EmployeeImport.FromExcel(bad, _db);
            Assert.IsNull(Emp("1002").ShiftId);
            Assert.IsTrue(result.Errors.Any(e => e.IndexOf("Night", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        [TestMethod]
        public void Import_ReadsEmailColumn()
        {
            var path = MakeXlsx(
                new[] { "Enroll #", "Name", "Email" },
                new[] { "1001", "Ali", "ali@example.com" });

            EmployeeImport.FromExcel(path, _db);

            Assert.AreEqual("ali@example.com", Emp("1001").Email);
        }

        [TestMethod]
        public void ActiveColumn_ParsesNoAsInactive()
        {
            var path = MakeXlsx(Header, new[] { "1001", "Ali", "", "", "", "", "40000", "", "No" });
            EmployeeImport.FromExcel(path, _db);

            Assert.IsFalse(Emp("1001").Active);
            Assert.AreEqual(40000, Emp("1001").Salary, 0.001);
        }
    }
}
