using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>Explicit scaffolder file groups; no membership is inferred from table sorting.</summary>
    internal static class Grouping
    {
        public static List<Group> LoadGroups(JObj raw, List<Table> tables)
        {
            var fields = new[] { "fileNumber", "tierPart", "tierTotalParts", "stagingFile", "guidFile" };
            var rawTableList = ((List<object>)raw.Get("tables")).Cast<JObj>().ToList();
            if (!raw.Has("files") && !rawTableList.Any(t => fields.Any(t.Has)))
                return new List<Group>();   // old manifests keep their one-package-per-table behavior

            Action<string> fail = m => { throw new GeneratorException("Invalid scaffolder grouping metadata: " + m); };
            var entries = raw.Get("files") as List<object>;
            if (entries == null || entries.Count == 0) fail("files[] with explicit staging/GUID membership is required");
            var byName = new Dictionary<string, Table>(StringComparer.Ordinal);
            foreach (var t in tables) byName[t.LogicalName.ToLowerInvariant()] = t;
            var rawTables = new Dictionary<string, JObj>(StringComparer.Ordinal);
            foreach (var t in rawTableList) rawTables[((string)t.Get("logicalName")).ToLowerInvariant()] = t;

            var files = new List<KeyValuePair<string, JObj>>();
            Func<string, JObj> file = n => files.Where(f => f.Key == n).Select(f => f.Value).FirstOrDefault();
            foreach (var e in entries)
            {
                var entry = e as JObj;
                if (entry == null) fail("files[] entry must be an object");
                var kind = entry.Get("kind") as string;
                if (kind != "staging" && kind != "guid") fail("unsupported file kind " + Repr(entry.Get("kind")));
                var name = entry.Get("fileName") as string;
                if (string.IsNullOrEmpty(name) || file(name.ToLowerInvariant()) != null) fail("missing or duplicate fileName " + Repr(entry.Get("fileName")));
                foreach (var check in new[] { Tuple.Create("tier", 0L), Tuple.Create("tierPart", 1L), Tuple.Create("tierTotalParts", 1L) })
                {
                    var v = entry.Get(check.Item1);
                    if (!(v is long) || (long)v < check.Item2) fail(name + ": invalid " + check.Item1);
                }
                if ((long)entry.Get("tierPart") > (long)entry.Get("tierTotalParts")) fail(name + ": tierPart exceeds tierTotalParts");
                var membersRaw = entry.Get("tables") as List<object>;
                if (membersRaw == null || membersRaw.Count == 0 || membersRaw.Any(n => !(n is string))) fail(name + ": nonempty tables[] is required");
                var members = membersRaw.Cast<string>().Select(n => n.ToLowerInvariant()).ToList();
                if (members.Distinct().Count() != members.Count || members.Any(n => !byName.ContainsKey(n)))
                    fail(name + ": duplicate or unknown table membership");
                var count = entry.Get("tableCount");
                if (!(count is long) || (long)count != members.Count) fail(name + ": tableCount does not match membership");
                files.Add(new KeyValuePair<string, JObj>(name.ToLowerInvariant(), entry));
            }

            var groups = new List<Group>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var usedFiles = new HashSet<string>(StringComparer.Ordinal);
            var numbers = new HashSet<long>();
            var slots = new HashSet<Tuple<long, long>>();
            foreach (var pairEntry in files)
            {
                var staging = pairEntry.Value;
                if ((string)staging.Get("kind") != "staging") continue;
                var members = ((List<object>)staging.Get("tables")).Cast<string>().Select(n => n.ToLowerInvariant()).ToList();
                var first = rawTables[members[0]];
                var guidName = first.Get("guidFile") as string;
                var guid = guidName != null ? file(guidName.ToLowerInvariant()) : null;
                var stagingName = (string)staging.Get("fileName");
                if (guid == null || (string)guid.Get("kind") != "guid") fail(stagingName + ": missing paired GUID file");
                if (!Py.SetEquals(((List<object>)guid.Get("tables")).Cast<string>().Select(n => n.ToLowerInvariant()), members))
                    fail(stagingName + ": staging/GUID memberships disagree");
                foreach (var key in new[] { "tier", "tierPart", "tierTotalParts" })
                    if ((long)staging.Get(key) != (long)guid.Get(key)) fail(stagingName + ": paired " + key + " disagrees");
                var numberObj = first.Get("fileNumber");
                if (!(numberObj is long) || (long)numberObj < 1 || numbers.Contains((long)numberObj))
                    fail("fileNumber must be a unique positive integer per pair");
                var number = (long)numberObj;
                var slot = Tuple.Create((long)staging.Get("tier"), (long)staging.Get("tierPart"));
                if (slots.Contains(slot)) fail("duplicate tier/part (" + slot.Item1 + ", " + slot.Item2 + ")");
                foreach (var member in members)
                {
                    var table = rawTables[member];
                    if (seen.Contains(member)) fail(member + ": table belongs to multiple groups");
                    var expected = new object[]
                    {
                        "fileNumber", number, "tier", staging.Get("tier"), "tierPart", staging.Get("tierPart"),
                        "tierTotalParts", staging.Get("tierTotalParts"), "stagingFile", stagingName, "guidFile", guid.Get("fileName"),
                    };
                    for (var i = 0; i < expected.Length; i += 2)
                    {
                        var key = (string)expected[i];
                        var actual = table.Get(key);
                        if (actual == null || actual.GetType() != expected[i + 1].GetType() || !actual.Equals(expected[i + 1]))
                            fail(member + ": missing or conflicting " + key);
                    }
                    seen.Add(member);
                }
                var pair = new[] { stagingName.ToLowerInvariant(), ((string)guid.Get("fileName")).ToLowerInvariant() };
                if (pair.Any(usedFiles.Contains)) fail("a file is reused by multiple groups");
                foreach (var p in pair) usedFiles.Add(p);
                numbers.Add(number);
                slots.Add(slot);
                groups.Add(new Group
                {
                    FileNumber = (int)number,
                    Tier = (int)(long)staging.Get("tier"),
                    TierPart = (int)(long)staging.Get("tierPart"),
                    TierTotalParts = (int)(long)staging.Get("tierTotalParts"),
                    StagingFile = stagingName,
                    GuidFile = (string)guid.Get("fileName"),
                    Tables = members.Select(n => byName[n]).ToList(),
                });
            }
            if (!seen.SetEquals(byName.Keys) || !usedFiles.SetEquals(files.Select(f => f.Key)))
                fail("every table and staging/GUID file must belong to exactly one pair");
            foreach (var tier in groups.Select(g => g.Tier).Distinct())
            {
                var tierGroups = groups.Where(g => g.Tier == tier).ToList();
                var totals = tierGroups.Select(g => g.TierTotalParts).Distinct().ToList();
                if (totals.Count != 1 || !new HashSet<int>(tierGroups.Select(g => g.TierPart)).SetEquals(Enumerable.Range(1, tierGroups[0].TierTotalParts)))
                    fail("tier " + tier + ": missing parts or conflicting tierTotalParts");
            }
            return groups.OrderBy(g => g.Tier).ThenBy(g => g.TierPart).ThenBy(g => g.FileNumber).ToList();
        }

        private static string Repr(object value)
        {
            if (value == null) return "None";
            var s = value as string;
            return s != null ? Py.Repr(s) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static List<Group> SelectGroups(List<Group> groups, List<Table> selected)
        {
            var wanted = new HashSet<string>(selected.Select(t => t.LogicalName.ToLowerInvariant()), StringComparer.Ordinal);
            return groups.Where(g => g.Tables.Any(t => wanted.Contains(t.LogicalName.ToLowerInvariant())))
                         .Select(g => g.With(g.Tables.Where(t => wanted.Contains(t.LogicalName.ToLowerInvariant())).ToList()))
                         .ToList();
        }
    }

    /// <summary>
    /// Reads the scaffolder's meta_seed.sql #Entity and #ColumnMap seed rows (literal
    /// INSERT ... VALUES rows only; the script is never executed).
    /// </summary>
    internal static class MetaSeed
    {
        private static readonly Regex InsertRe = Py.Re(@"INSERT\s+INTO\s+#(Entity|ColumnMap)\s*\(([^)]*)\)\s*VALUES", RegexOptions.IgnoreCase);
        private static readonly Regex TokenRe = Py.Re(@"\G\s*(?:N?'((?:[^']|'')*)'|(NULL)\b|(-?\d+))\s*", RegexOptions.IgnoreCase);

        public static Dictionary<string, List<Dictionary<string, object>>> Load(string path)
        {
            if (!File.Exists(path)) throw new GeneratorException("Scaffolder meta seed not found: " + path);
            return LoadText(Metadata.ReadText(path), Path.GetFileName(path));
        }

        public static Dictionary<string, List<Dictionary<string, object>>> LoadText(string text, string name)
        {
            text = Metadata.NormalizeText(text);
            var rows = new Dictionary<string, List<Dictionary<string, object>>>
            {
                { "Entity", new List<Dictionary<string, object>>() },
                { "ColumnMap", new List<Dictionary<string, object>>() },
            };
            foreach (Match match in InsertRe.Matches(text))
            {
                var table = match.Groups[1].Value.ToLowerInvariant() == "entity" ? "Entity" : "ColumnMap";
                var names = match.Groups[2].Value.Split(',').Select(n => n.Trim()).ToList();
                var pos = match.Index + match.Length;
                while (true)
                {
                    pos = SkipSpace(text, pos);
                    if (pos >= text.Length || text[pos] != '(')
                        throw new GeneratorException(name + ": unreadable #" + table + " VALUES row near offset " + pos);
                    var values = Row(text, ref pos, name, table);
                    if (values.Count != names.Count)
                        throw new GeneratorException(name + ": #" + table + " row has " + values.Count + " values for " + names.Count + " columns");
                    var row = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (var i = 0; i < names.Count; i++) row[names[i]] = values[i];
                    rows[table].Add(row);
                    pos = SkipSpace(text, pos);
                    if (pos < text.Length && text[pos] == ',') { pos++; continue; }
                    if (pos < text.Length && text[pos] == ';') break;
                    throw new GeneratorException(name + ": #" + table + " VALUES list is not terminated by ';'");
                }
            }
            if (rows["Entity"].Count == 0 || rows["ColumnMap"].Count == 0)
                throw new GeneratorException(name + ": no #Entity/#ColumnMap seed rows found");
            return rows;
        }

        private static int SkipSpace(string text, int pos)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            return pos;
        }

        private static List<object> Row(string text, ref int pos, string name, string table)
        {
            pos++;   // past '('
            var values = new List<object>();
            while (true)
            {
                var m = TokenRe.Match(text, pos);
                if (!m.Success) throw new GeneratorException(name + ": unsupported #" + table + " value near offset " + pos);
                if (m.Groups[1].Success) values.Add(m.Groups[1].Value.Replace("''", "'"));
                else if (m.Groups[2].Success) values.Add(null);
                else values.Add(long.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
                pos = m.Index + m.Length;
                var following = pos < text.Length ? text[pos] : '\0';
                if (following == ',') pos++;
                else if (following == ')') { pos++; return values; }
                else throw new GeneratorException(name + ": unreadable #" + table + " row near offset " + pos);
            }
        }
    }
}
