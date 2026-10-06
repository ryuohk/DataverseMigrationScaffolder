using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>A pipeline column type as written in .dtsx (e.g. dataType="wstr" length="100").</summary>
    internal sealed class SsisType
    {
        public SsisType(string dataType, int? length = null, int? precision = null, int? scale = null, int? codePage = null)
        {
            DataType = dataType; Length = length; Precision = precision; Scale = scale; CodePage = codePage;
        }

        public string DataType { get; }
        public int? Length { get; }
        public int? Precision { get; }
        public int? Scale { get; }
        public int? CodePage { get; }
    }

    internal sealed class Column
    {
        public string Name;
        public string SqlType;
        public string DataverseType;
        public SsisType SsisType;
        public bool IsPrimaryId;
        public bool IsLookup;
        public List<string> LookupTargets = new List<string>();
        public bool RequiresDeferredUpdate;
        public bool IsPolymorphic;
        /// <summary>Every Dataverse target of a lookup, in scope or not (KingswaySoft LookupTypes).</summary>
        public List<string> AllLookupTargets = new List<string>();

        /// <summary>Dataverse computes &lt;money&gt;_base from the money value; it cannot be written.</summary>
        public bool IsReadOnlyCompanion { get { return DataverseType == "Money" && Name.EndsWith("_base", StringComparison.Ordinal); } }
    }

    internal sealed class Table
    {
        public string LogicalName;
        public string SchemaName;
        public string DisplayName;
        public int Tier;
        public string StagingTable;
        public string GuidTable;
        public string PrimaryId;
        public string PrimaryName;
        public List<string> MatchKeys = new List<string>();
        public List<Column> Columns = new List<Column>();
        /// <summary>In-scope lookup targets; dropped ones are cycle edges resolved by a deferred pass.</summary>
        public List<string> Dependencies = new List<string>();
        public List<string> DroppedDependencies = new List<string>();
        public List<string> ExternalDependencies = new List<string>();

        /// <summary>Columns carried through the generated data flow, in manifest order.</summary>
        public List<Column> PackageColumns { get { return Columns.Where(c => !c.IsReadOnlyCompanion).ToList(); } }
        public List<Column> SkippedColumns { get { return Columns.Where(c => c.IsReadOnlyCompanion).ToList(); } }

        public Table With(string displayName = null, List<Column> columns = null)
        {
            var copy = (Table)MemberwiseClone();
            if (displayName != null) copy.DisplayName = displayName;
            if (columns != null) copy.Columns = columns;
            return copy;
        }
    }

    internal sealed class Group
    {
        public int FileNumber;
        public int Tier;
        public int TierPart;
        public int TierTotalParts;
        public string StagingFile;
        public string GuidFile;
        public List<Table> Tables;

        public Group With(List<Table> tables)
        {
            var copy = (Group)MemberwiseClone();
            copy.Tables = tables;
            return copy;
        }
    }

    internal sealed class Manifest
    {
        public string Path;
        public List<Table> Tables;
        public List<object> Cycles;
        public List<Group> Groups;
        public string StagingPrefix = "stage_";

        public Table Table(string logicalName)
        {
            var wanted = (logicalName ?? "").ToLowerInvariant();
            return Tables.FirstOrDefault(t => t.LogicalName.ToLowerInvariant() == wanted);
        }
    }

    internal static class Metadata
    {
        private static readonly string[] RequiredTableKeys =
            { "logicalName", "schemaName", "tier", "stagingTable", "guidTable", "primaryIdAttribute", "columns" };
        private static readonly string[] RequiredColumnKeys = { "name", "sqlType", "dataverseType" };
        private static readonly Regex SqlTypeRe = Py.Start(@"\s*([A-Za-z0-9]+)\s*(?:\(\s*([^)]*?)\s*\))?\s*$");

        /// <summary>Map a staging-table SQL type to the SSIS pipeline type used for its column.</summary>
        public static SsisType SsisTypeFor(string sqlType)
        {
            var m = SqlTypeRe.Match(sqlType ?? "");
            if (!m.Success) throw new ArgumentException("unparseable SQL type " + Py.Repr(sqlType));
            var b = m.Groups[1].Value.ToUpperInvariant();
            var args = m.Groups[2].Success && m.Groups[2].Value.Length > 0
                ? m.Groups[2].Value.Split(',').Select(a => a.Trim()).ToList()
                : new List<string>();
            Func<int, int, int> arg = (i, fallback) => args.Count > i ? int.Parse(args[i], CultureInfo.InvariantCulture) : fallback;

            if (b == "NVARCHAR" || b == "NCHAR")
            {
                if (args.Count > 0 && args[0].ToUpperInvariant() == "MAX") return new SsisType("nText");
                return new SsisType("wstr", length: arg(0, 1));
            }
            if (b == "VARCHAR" || b == "CHAR")
            {
                if (args.Count > 0 && args[0].ToUpperInvariant() == "MAX") return new SsisType("text", codePage: 1252);
                return new SsisType("str", length: arg(0, 1), codePage: 1252);
            }
            if (b == "DECIMAL" || b == "NUMERIC") return new SsisType("numeric", precision: arg(0, 18), scale: arg(1, 0));
            if (b == "DATETIME2") return new SsisType("dbTimeStamp2", scale: arg(0, 7));
            var simple = new Dictionary<string, string>
            {
                { "BIT", "bool" }, { "TINYINT", "ui1" }, { "SMALLINT", "i2" }, { "INT", "i4" }, { "BIGINT", "i8" },
                { "REAL", "r4" }, { "FLOAT", "r8" }, { "MONEY", "cy" }, { "DATE", "dbDate" },
                { "DATETIME", "dbTimeStamp" }, { "UNIQUEIDENTIFIER", "guid" },
            };
            string simpleType;
            if (simple.TryGetValue(b, out simpleType) && args.Count == 0) return new SsisType(simpleType);
            throw new ArgumentException("unsupported SQL type " + Py.Repr(sqlType));
        }

        public static Manifest LoadManifest(string path)
        {
            if (!File.Exists(path)) throw new GeneratorException("Scaffolder manifest not found: " + path);
            return LoadManifestText(ReadText(path), path);
        }

        /// <summary>A manifest from its JSON text; label names it in messages (a path, or a description
        /// of an in-memory manifest).</summary>
        public static Manifest LoadManifestText(string text, string path)
        {
            object raw;
            try { raw = Json.Parse(NormalizeText(text)); }
            catch (FormatException ex) { throw new GeneratorException("Scaffolder manifest is not valid JSON (" + path + "): " + ex.Message, ex); }
            var obj = raw as JObj;
            var tablesRaw = obj == null ? null : obj.Get("tables") as List<object>;
            if (tablesRaw == null) throw new GeneratorException("Scaffolder manifest has no 'tables' array: " + path);
            if (tablesRaw.Count == 0) throw new GeneratorException("Scaffolder manifest lists no tables: " + path);

            var tables = new List<Table>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < tablesRaw.Count; i++)
            {
                var table = LoadTable(tablesRaw[i], i, path);
                if (!seen.Add(table.LogicalName.ToLowerInvariant()))
                    throw new GeneratorException(path + ": table " + Py.Repr(table.LogicalName) + " is listed more than once");
                tables.Add(table);
            }
            var declared = obj.Get("tableCount");
            if (declared is long && (long)declared != tables.Count)
                throw new GeneratorException(path + ": tableCount is " + declared + " but the 'tables' array has " + tables.Count + " entries");
            var groups = Grouping.LoadGroups(obj, tables);
            var options = obj.Get("options") as JObj;
            var prefix = options != null && options.Get("stagingPrefix") is string ? (string)options.Get("stagingPrefix") : "stage_";
            var cycles = obj.Get("cycles") as List<object> ?? new List<object>();
            return new Manifest { Path = path, Tables = tables, Cycles = cycles, Groups = groups, StagingPrefix = prefix };
        }

        /// <summary>A text file as UTF-8 without a leading BOM, with every line break read as "\n".</summary>
        public static string ReadText(string path)
        {
            return NormalizeText(new UTF8Encoding(false).GetString(File.ReadAllBytes(path)));
        }

        /// <summary>Text as it reads from a file: no leading BOM, every line break "\n".</summary>
        public static string NormalizeText(string text)
        {
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>Write a text file as UTF-8 (no BOM) with Windows line breaks.</summary>
        public static void WriteText(string path, string text)
        {
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text.Replace("\n", "\r\n")));
        }

        private static bool Empty(object v) { return v == null || (v is string && (string)v == ""); }

        private static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is string) return ((string)v).Length > 0;
            if (v is long) return (long)v != 0;
            if (v is double) return (double)v != 0;
            var list = v as System.Collections.ICollection;
            return list == null || list.Count > 0;
        }

        private static List<string> Strings(object v)
        {
            var list = v as List<object>;
            return list == null ? new List<string>() : list.Select(x => x as string).ToList();
        }

        private static Table LoadTable(object entryObj, int index, string path)
        {
            var where = path + ": tables[" + index + "]";
            var entry = entryObj as JObj;
            if (entry == null) throw new GeneratorException(where + " is not an object");
            var missing = RequiredTableKeys.Where(k => Empty(entry.Get(k))).ToList();
            if (missing.Count > 0)
            {
                var name = entry.Get("logicalName") as string;
                throw new GeneratorException(where + " (" + (string.IsNullOrEmpty(name) ? "?" : name) + ") is missing required field(s): " + string.Join(", ", missing));
            }
            var logical = (string)entry.Get("logicalName");
            where = path + ": table " + Py.Repr(logical);
            var tier = entry.Get("tier");
            if (!(tier is long) || (long)tier < 0) throw new GeneratorException(where + " has an invalid tier " + Convert.ToString(tier, CultureInfo.InvariantCulture));
            var columnsRaw = entry.Get("columns") as List<object>;
            if (columnsRaw == null || columnsRaw.Count == 0) throw new GeneratorException(where + " has no columns");

            var columns = new List<Column>();
            for (var ci = 0; ci < columnsRaw.Count; ci++)
            {
                var col = columnsRaw[ci] as JObj;
                if (col == null) throw new GeneratorException(where + ": columns[" + ci + "] is not an object");
                var cMissing = RequiredColumnKeys.Where(k => !Truthy(col.Get(k))).ToList();
                if (cMissing.Count > 0)
                {
                    var cname = col.Get("name") as string;
                    throw new GeneratorException(where + ": columns[" + ci + "] (" + (string.IsNullOrEmpty(cname) ? "?" : cname) + ") is missing " + string.Join(", ", cMissing));
                }
                SsisType ssis;
                try { ssis = SsisTypeFor((string)col.Get("sqlType")); }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException)
                {
                    throw new GeneratorException(where + ", column " + Py.Repr((string)col.Get("name")) + ": " + ex.Message, ex);
                }
                var inScope = Strings(col.Get("targetsInScope"));
                var all = Strings(col.Get("targets"));
                columns.Add(new Column
                {
                    Name = (string)col.Get("name"),
                    SqlType = (string)col.Get("sqlType"),
                    DataverseType = (string)col.Get("dataverseType"),
                    SsisType = ssis,
                    IsPrimaryId = Truthy(col.Get("isPrimaryId")),
                    IsLookup = Truthy(col.Get("isLookup")),
                    LookupTargets = inScope.Count > 0 ? inScope : all,
                    RequiresDeferredUpdate = col.Get("requiresDeferredUpdate") is bool && (bool)col.Get("requiresDeferredUpdate"),
                    IsPolymorphic = col.Get("isPolymorphic") is bool && (bool)col.Get("isPolymorphic"),
                    AllLookupTargets = all.Count > 0 ? all : inScope,
                });
            }
            var names = columns.Select(c => c.Name.ToLowerInvariant()).ToList();
            var dupes = Py.Sorted(names.Where(n => names.Count(x => x == n) > 1).Distinct());
            if (dupes.Count > 0) throw new GeneratorException(where + " has duplicate column(s): " + string.Join(", ", dupes));

            var display = entry.Get("displayName") as string;
            return new Table
            {
                LogicalName = logical,
                SchemaName = (string)entry.Get("schemaName"),
                DisplayName = string.IsNullOrEmpty(display) ? (string)entry.Get("schemaName") : display,
                Tier = (int)(long)tier,
                StagingTable = (string)entry.Get("stagingTable"),
                GuidTable = (string)entry.Get("guidTable"),
                PrimaryId = (string)entry.Get("primaryIdAttribute"),
                PrimaryName = entry.Get("primaryNameAttribute") as string,
                MatchKeys = Strings(entry.Get("matchKeys")),
                Columns = columns,
                Dependencies = Names(entry, "dependencies", where),
                DroppedDependencies = Names(entry, "droppedDependencies", where),
                ExternalDependencies = Names(entry, "externalDependencies", where),
            };
        }

        private static List<string> Names(JObj entry, string key, string where)
        {
            var value = entry.Get(key);
            if (!Truthy(value)) return new List<string>();
            var list = value as List<object>;
            if (list == null || list.Any(v => !(v is string) || ((string)v).Length == 0))
                throw new GeneratorException(where + ": " + key + " must be a list of table names");
            return list.Cast<string>().ToList();
        }

        /// <summary>'[dbo].[stage_Account]' -> 'stage_Account'.</summary>
        public static string ObjectName(string qualified)
        {
            var last = qualified.Split('.').Last();
            return last.Trim().Trim('[', ']');
        }
    }
}
