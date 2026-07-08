using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>Cell styles (indexes into cellXfs in styles.xml).</summary>
    public static class XlsxStyle
    {
        public const int Normal = 0;      // no formatting
        public const int Bold = 1;        // bold, no fill
        public const int Header = 2;      // bold + teal fill + border (labels / header rows)
        public const int Body = 3;        // thin border
        public const int BodyWrap = 4;    // thin border + wrapped text
    }

    public class XlsxCell
    {
        public string Text;
        public int Style;

        public XlsxCell(string text, int style)
        {
            Text = text ?? "";
            Style = style;
        }
    }

    public class XlsxRow
    {
        public List<XlsxCell> Cells = new List<XlsxCell>();

        public XlsxRow Add(string text)
        {
            Cells.Add(new XlsxCell(text, XlsxStyle.Body));
            return this;
        }

        public XlsxRow AddBold(string text)
        {
            Cells.Add(new XlsxCell(text, XlsxStyle.Header));
            return this;
        }

        public XlsxRow AddWrap(string text)
        {
            Cells.Add(new XlsxCell(text, XlsxStyle.BodyWrap));
            return this;
        }
    }

    public class XlsxSheet
    {
        public string Name;
        public double[] ColumnWidths;    // optional, per column starting at A
        public List<XlsxRow> Rows = new List<XlsxRow>();

        public XlsxRow AddRow()
        {
            var row = new XlsxRow();
            Rows.Add(row);
            return row;
        }
    }

    /// <summary>
    /// Minimal .xlsx writer (inline strings, small fixed style set) built on ZipArchive,
    /// so the plugin stays a single DLL with no extra NuGet dependencies to deploy.
    /// </summary>
    public static class XlsxWriter
    {
        private const string FillColor = "FFCDE6E4";   // pale teal, like the sample dictionary

        public static byte[] Write(IList<XlsxSheet> sheets)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    AddEntry(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
                    AddEntry(zip, "_rels/.rels",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                        "</Relationships>");
                    AddEntry(zip, "xl/workbook.xml", Workbook(sheets));
                    AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
                    AddEntry(zip, "xl/styles.xml", Styles());
                    for (var i = 0; i < sheets.Count; i++)
                    {
                        AddEntry(zip, string.Format("xl/worksheets/sheet{0}.xml", i + 1), Worksheet(sheets[i]));
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>Excel sheet names: max 31 chars, no []:*?/\ and unique within the workbook.</summary>
        public static string SafeSheetName(string name, HashSet<string> used)
        {
            var cleaned = new string((name ?? "Sheet").Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
            if (cleaned.Length == 0) cleaned = "Sheet";
            if (cleaned.Length > 31) cleaned = cleaned.Substring(0, 31);

            var candidate = cleaned;
            var n = 2;
            while (used.Contains(candidate.ToLowerInvariant()))
            {
                var suffix = " (" + n + ")";
                candidate = (cleaned.Length + suffix.Length > 31 ? cleaned.Substring(0, 31 - suffix.Length) : cleaned) + suffix;
                n++;
            }
            used.Add(candidate.ToLowerInvariant());
            return candidate;
        }

        private static void AddEntry(ZipArchive zip, string path, string content)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string ContentTypes(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (var i = 1; i <= sheetCount; i++)
            {
                sb.AppendFormat("<Override PartName=\"/xl/worksheets/sheet{0}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>", i);
            }
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string Workbook(IList<XlsxSheet> sheets)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<sheets>");
            for (var i = 0; i < sheets.Count; i++)
            {
                sb.AppendFormat("<sheet name=\"{0}\" sheetId=\"{1}\" r:id=\"rId{1}\"/>", Esc(sheets[i].Name), i + 1);
            }
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string WorkbookRels(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 1; i <= sheetCount; i++)
            {
                sb.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{0}.xml\"/>", i);
            }
            sb.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>", sheetCount + 1);
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string Styles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<fonts count=\"2\">" +
                   "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                   "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                   "</fonts>" +
                   "<fills count=\"3\">" +
                   "<fill><patternFill patternType=\"none\"/></fill>" +
                   "<fill><patternFill patternType=\"gray125\"/></fill>" +
                   "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + FillColor + "\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
                   "</fills>" +
                   "<borders count=\"2\">" +
                   "<border/>" +
                   "<border><left style=\"thin\"><color auto=\"1\"/></left><right style=\"thin\"><color auto=\"1\"/></right>" +
                   "<top style=\"thin\"><color auto=\"1\"/></top><bottom style=\"thin\"><color auto=\"1\"/></bottom></border>" +
                   "</borders>" +
                   "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
                   "<cellXfs count=\"5\">" +
                   "<xf xfId=\"0\"/>" +                                                                                       // 0 normal
                   "<xf fontId=\"1\" applyFont=\"1\" xfId=\"0\"/>" +                                                          // 1 bold
                   "<xf fontId=\"1\" fillId=\"2\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" xfId=\"0\"/>" +  // 2 header
                   "<xf borderId=\"1\" applyBorder=\"1\" xfId=\"0\"/>" +                                                      // 3 body
                   "<xf borderId=\"1\" applyBorder=\"1\" applyAlignment=\"1\" xfId=\"0\"><alignment wrapText=\"1\" vertical=\"top\"/></xf>" + // 4 body wrap
                   "</cellXfs>" +
                   "</styleSheet>";
        }

        private static string Worksheet(XlsxSheet sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            if (sheet.ColumnWidths != null && sheet.ColumnWidths.Length > 0)
            {
                sb.Append("<cols>");
                for (var i = 0; i < sheet.ColumnWidths.Length; i++)
                {
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "<col min=\"{0}\" max=\"{0}\" width=\"{1}\" customWidth=\"1\"/>", i + 1, sheet.ColumnWidths[i]);
                }
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var cells = sheet.Rows[r].Cells;
                var height = EstimateRowHeight(sheet, cells);

                if (height > 0)
                {
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "<row r=\"{0}\" ht=\"{1}\" customHeight=\"1\">", r + 1, height);
                }
                else
                {
                    sb.AppendFormat("<row r=\"{0}\">", r + 1);
                }

                for (var c = 0; c < cells.Count; c++)
                {
                    if (string.IsNullOrEmpty(cells[c].Text) && cells[c].Style == XlsxStyle.Normal) continue;
                    sb.AppendFormat("<c r=\"{0}{1}\"{2} t=\"inlineStr\"><is><t xml:space=\"preserve\">{3}</t></is></c>",
                        ColumnLetter(c), r + 1,
                        cells[c].Style != XlsxStyle.Normal ? string.Format(" s=\"{0}\"", cells[c].Style) : "",
                        Esc(cells[c].Text));
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        /// <summary>Rough height for rows containing wrapped cells so the text is visible without manual autofit.</summary>
        private static double EstimateRowHeight(XlsxSheet sheet, List<XlsxCell> cells)
        {
            var maxLines = 1;
            for (var c = 0; c < cells.Count; c++)
            {
                if (cells[c].Style != XlsxStyle.BodyWrap || string.IsNullOrEmpty(cells[c].Text)) continue;
                var width = sheet.ColumnWidths != null && c < sheet.ColumnWidths.Length ? sheet.ColumnWidths[c] : 50;
                var charsPerLine = Math.Max(10, (int)width - 2);
                var lines = cells[c].Text.Split('\n').Sum(part => 1 + part.Length / charsPerLine);
                if (lines > maxLines) maxLines = Math.Min(10, lines);
            }
            return maxLines > 1 ? maxLines * 14.5 : 0;
        }

        private static string ColumnLetter(int index)
        {
            var letters = "";
            index++;
            while (index > 0)
            {
                var rem = (index - 1) % 26;
                letters = (char)('A' + rem) + letters;
                index = (index - 1) / 26;
            }
            return letters;
        }

        private static string Esc(string value)
        {
            return (value ?? "")
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
