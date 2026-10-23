using System;
using System.Data;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using UiPath.CodedWorkflows;

namespace Wintrust.Application.FirstInsite.API
{
    public class CWF_ReadMappingTables_Aon : CodedWorkflow
    {
        [Workflow]
        public (DataTable out_dtAgents, DataTable out_dtCoverages, DataTable out_dtCarriers,
            bool out_bSuccess, string out_sErrorMessage, Exception out_exSystemException) Execute(string in_sExcelFilePath)
        {
            try
            {
                if (!File.Exists(in_sExcelFilePath)) throw new FileNotFoundException("Aon mapping workbook was not found.", in_sExcelFilePath);
                using (var book = new XLWorkbook(in_sExcelFilePath))
                {
                    // Coverages is authoritative; ignore the duplicate coverage block on Agents.
                    var agents = ReadBlock(book.Worksheet("Agents"), "Agents", "Aon Office", "Agent Code", "Location");
                    var coverages = ReadBlock(book.Worksheet("Coverages"), "Coverages", "Coverage Input", "InSite Code");
                    var carriers = ReadBlock(book.Worksheet("Agents"), "Carriers", "Market Name", "Carrier Code", "General Agent Code");
                    CSF_MappingHelper_Aon.ValidateTable(agents, CSF_MappingHelper_Aon.OfficeColumn(agents), "Agent Code");
                    CSF_MappingHelper_Aon.ValidateTable(coverages, "Coverage Input", "InSite Code");
                    CSF_MappingHelper_Aon.ValidateTable(carriers, "Market Name", "Carrier Code", "General Agent Code");
                    return (agents, coverages, carriers, true, "", null);
                }
            }
            catch (Exception ex) { return (new DataTable(), new DataTable(), new DataTable(), false, ex.Message, ex); }
        }

        private static DataTable ReadBlock(IXLWorksheet sourceSheet, string name, params string[] headers)
        {
            var anchors = sourceSheet.CellsUsed(XLCellsUsedOptions.Contents)
                .Where(c => Header(c) == Normalize(headers[0])).ToList();
            if (anchors.Count != 1)
                throw new ArgumentException("Expected one Aon " + name + " mapping block; found " + anchors.Count + ".");
            var anchor = anchors[0];
            var sheet = anchor.Worksheet;
            int firstRow = anchor.Address.RowNumber;
            var columns = headers.Select(h => sheet.Row(firstRow).CellsUsed(XLCellsUsedOptions.Contents)
                .Where(c => Header(c) == Normalize(h)).ToList()).ToList();
            for (int i = 0; i < columns.Count; i++)
                if (columns[i].Count != 1)
                    throw new ArgumentException(name + ": missing or duplicate mapping header '" + headers[i] + "'.");

            var table = new DataTable(name);
            foreach (string header in headers) table.Columns.Add(header);
            int lastRow = columns.Max(c => sheet.Column(c[0].Address.ColumnNumber)
                .LastCellUsed(XLCellsUsedOptions.Contents).Address.RowNumber);
            for (int row = firstRow + 1; row <= lastRow; row++)
            {
                string[] values = columns.Select(c => MappingText(sheet.Cell(row, c[0].Address.ColumnNumber))).ToArray();
                if (values.Any(v => v.Length > 0)) table.Rows.Add(values.Cast<object>().ToArray());
            }
            return table;
        }

        private static string Normalize(string text)
        {
            return Regex.Replace(text ?? "", "[^a-zA-Z0-9]", "").ToLowerInvariant();
        }

        private static string Header(IXLCell cell)
        {
            // Errors in data cells are checked when their block is read, not while locating headers.
            var value = cell.HasFormula ? cell.CachedValue : cell.Value;
            string header = Normalize(value.ToString(CultureInfo.InvariantCulture));
            return header == "office" ? "aonoffice" : header;
        }

        private static string MappingText(IXLCell cell)
        {
            var value = cell.HasFormula ? cell.CachedValue : cell.Value;
            if (value.IsError || (cell.HasFormula && value.IsBlank))
                throw new ArgumentException(cell.Worksheet.Name + " " + cell.Address + ": mapping cell has an error or no stored formula result.");
            if (!cell.HasFormula) return cell.GetFormattedString().Trim();
            string format = cell.Style.NumberFormat.Format ?? "";
            return value.IsNumber && Regex.IsMatch(format, "^0+$")
                ? value.GetNumber().ToString(format, CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture).Trim();
        }
    }
}
