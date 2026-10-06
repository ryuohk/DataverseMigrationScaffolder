using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DataverseMigrationScaffolder.HarnessGen
{
    internal sealed class DeferredPass
    {
        public Table Table;
        public Column Key;                              // the table's primary id: the update targets existing rows only
        public List<Column> Columns;                    // deferred lookup columns, manifest order
        public List<string> DroppedTargets;
        public bool? MetaEnabled;                       // meta.Entity IsEnabled for PassNo 2; null without meta_seed.sql
        public List<string> MetaUnwritten = new List<string>();   // PassNo 2 rows with IncludeOnUpdate = 0
        /// <summary>&lt;lookup&gt;type (EntityName) columns of polymorphic deferred lookups: KingswaySoft
        /// needs the target table of each value, so they are sent with the lookup.</summary>
        public List<Column> TypeColumns = new List<Column>();

        public List<Column> UpdateColumns { get { return Columns.Concat(TypeColumns).ToList(); } }

        public DeferredPass Copy() { return (DeferredPass)MemberwiseClone(); }
    }

    /// <summary>
    /// Deferred lookup update passes (scaffolder PassNo 2) for dependency-cycle members. A cycle
    /// member is loaded before some of its lookup targets; the scaffolder records the dropped
    /// edges (cycles[] / droppedDependencies), the columns to set afterwards
    /// (requiresDeferredUpdate) and, in meta_seed.sql, the PassNo 2 column maps.
    /// </summary>
    internal static class Deferred
    {
        public static List<DeferredPass> Plan(Manifest manifest, List<Table> selected, Dictionary<string, List<Dictionary<string, object>>> seed)
        {
            var known = new HashSet<string>(manifest.Tables.Select(t => t.LogicalName.ToLowerInvariant()), StringComparer.Ordinal);
            var cycles = new Dictionary<string, JObj>(StringComparer.Ordinal);
            foreach (var entryObj in manifest.Cycles)
            {
                var entry = entryObj as JObj;
                var name = entry != null ? entry.Get("table") as string : null;
                var targets = entry != null ? entry.Get("droppedTargets") as List<object> : null;
                if (name == null || !known.Contains(name.ToLowerInvariant()) || cycles.ContainsKey(name.ToLowerInvariant())
                    || targets == null || targets.Count == 0 || targets.Any(t => !(t is string) || !known.Contains(((string)t).ToLowerInvariant())))
                    throw new GeneratorException("Invalid manifest cycles[] entry " + DescribeEntry(entryObj)
                                                 + ": expected a unique scaffolded table and its nonempty droppedTargets");
                cycles[name.ToLowerInvariant()] = entry;
            }

            var passes = new List<DeferredPass>();
            foreach (var table in selected)
            {
                var name = table.LogicalName.ToLowerInvariant();
                var deferred = table.Columns.Where(c => c.RequiresDeferredUpdate).ToList();
                if (!cycles.ContainsKey(name) && deferred.Count == 0 && table.DroppedDependencies.Count == 0) continue;
                var current = table;
                Action<string> fail = m => { throw new GeneratorException("Deferred update pass for " + Py.Repr(current.LogicalName) + ": " + m); };

                if (!cycles.ContainsKey(name)) fail("droppedDependencies or requiresDeferredUpdate columns without a manifest cycles[] entry");
                var targets = ((List<object>)cycles[name].Get("droppedTargets")).Cast<string>().ToList();
                var lowered = new HashSet<string>(targets.Select(t => t.ToLowerInvariant()), StringComparer.Ordinal);
                if (table.DroppedDependencies.Count > 0 && !lowered.SetEquals(table.DroppedDependencies.Select(t => t.ToLowerInvariant())))
                    fail("droppedDependencies " + Py.Repr(table.DroppedDependencies) + " disagree with cycles[] " + Py.Repr(targets));
                if (table.Dependencies.Count > 0 && !lowered.IsSubsetOf(table.Dependencies.Select(d => d.ToLowerInvariant())))
                    fail("cycles[] lists dropped targets that are not in the table's dependencies");
                if (deferred.Count == 0) fail("no column is marked requiresDeferredUpdate, so the lookups to update are unknown");
                var covered = new HashSet<string>(StringComparer.Ordinal);
                var byName = new Dictionary<string, Column>(StringComparer.Ordinal);
                foreach (var c in table.Columns) byName[c.Name.ToLowerInvariant()] = c;
                var typeColumns = new List<Column>();
                foreach (var column in deferred)
                {
                    if (!column.IsLookup || column.LookupTargets.Count == 0)
                        fail("deferred column " + Py.Repr(column.Name) + " is not a lookup with an in-scope target");
                    if (column.IsPolymorphic || column.LookupTargets.Count != 1)
                    {
                        // Customer/Owner-style lookups (e.g. contact.parentcustomerid -> account or
                        // contact) carry their target table in the scaffolder's <lookup>type column.
                        Column typeColumn;
                        byName.TryGetValue((column.Name + "type").ToLowerInvariant(), out typeColumn);
                        if (typeColumn == null || typeColumn.DataverseType != "EntityName")
                            fail("deferred column " + Py.Repr(column.Name) + " is polymorphic, but the table has no "
                                 + column.Name + "type column (EntityName) naming each value's target table");
                        if (typeColumn.RequiresDeferredUpdate) fail("type column " + Py.Repr(typeColumn.Name) + " is itself marked requiresDeferredUpdate");
                        if (!typeColumns.Contains(typeColumn)) typeColumns.Add(typeColumn);
                    }
                    var droppedHere = column.LookupTargets.Where(t => lowered.Contains(t.ToLowerInvariant())).ToList();
                    if (droppedHere.Count == 0)
                        fail("deferred column " + Py.Repr(column.Name) + " targets " + string.Join(", ", column.LookupTargets)
                             + ", none of which is a dropped dependency");
                    foreach (var t in droppedHere) covered.Add(t.ToLowerInvariant());
                }
                if (!covered.SetEquals(lowered))
                    fail("no deferred column resolves dropped target(s) " + Py.Repr(Py.Sorted(lowered.Where(t => !covered.Contains(t)))));
                var key = table.Columns.FirstOrDefault(c => c.Name.ToLowerInvariant() == table.PrimaryId.ToLowerInvariant());
                if (key == null || key.RequiresDeferredUpdate || key.IsReadOnlyCompanion)
                    fail("primary id column " + Py.Repr(table.PrimaryId) + " is not a staging column, so existing rows cannot be identified");
                passes.Add(new DeferredPass { Table = table, Key = key, Columns = deferred, DroppedTargets = targets, TypeColumns = typeColumns });
            }
            if (seed != null) passes = CrossCheck(passes, selected, seed);
            return passes;
        }

        private static string DescribeEntry(object entry)
        {
            var obj = entry as JObj;
            if (obj == null) return entry == null ? "None" : Convert.ToString(entry, CultureInfo.InvariantCulture);
            return "{" + string.Join(", ", obj.Select(kv => Py.Repr(kv.Key) + ": " + ValueRepr(kv.Value))) + "}";
        }

        private static string ValueRepr(object v)
        {
            if (v == null) return "None";
            if (v is string) return Py.Repr((string)v);
            if (v is bool) return (bool)v ? "True" : "False";
            var list = v as List<object>;
            if (list != null) return "[" + string.Join(", ", list.Select(ValueRepr)) + "]";
            if (v is JObj) return DescribeEntry(v);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static string Str(object v) { return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        private static bool IsOne(object v) { return v is long && (long)v == 1; }
        private static bool IsTwo(object v) { return v is long && (long)v == 2; }
        private static object Get(Dictionary<string, object> row, string key) { object v; return row.TryGetValue(key, out v) ? v : null; }

        /// <summary>Require meta_seed.sql PassNo 2 rows to describe exactly the manifest's deferred passes.</summary>
        private static List<DeferredPass> CrossCheck(List<DeferredPass> passes, List<Table> selected, Dictionary<string, List<Dictionary<string, object>>> seed)
        {
            var entities = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var row in seed["Entity"])
            {
                foreach (var required in new[] { "LogicalName", "PassNo" })
                    if (!row.ContainsKey(required)) throw new GeneratorException("meta_seed.sql #Entity rows have no " + Py.Repr(required) + " column");
                entities[Str(row["LogicalName"]).ToLowerInvariant() + "|" + Str(row["PassNo"])] = row;
            }
            var planned = new HashSet<string>(passes.Select(p => p.Table.LogicalName.ToLowerInvariant()), StringComparer.Ordinal);
            var extra = Py.Sorted(selected.Where(t => entities.ContainsKey(t.LogicalName.ToLowerInvariant() + "|2") && !planned.Contains(t.LogicalName.ToLowerInvariant()))
                                          .Select(t => t.LogicalName));
            if (extra.Count > 0)
                throw new GeneratorException("meta_seed.sql has PassNo 2 rows for " + string.Join(", ", extra) + ", but the manifest marks no deferred lookups for them");
            var checkedPasses = new List<DeferredPass>();
            foreach (var p in passes)
            {
                var name = p.Table.LogicalName;
                Action<string> fail = m => { throw new GeneratorException("Deferred update pass for " + Py.Repr(name) + ": meta_seed.sql " + m); };
                Dictionary<string, object> entity;
                if (!entities.TryGetValue(name.ToLowerInvariant() + "|2", out entity)) fail("has no PassNo 2 meta.Entity row");
                foreach (var check in new[] { Tuple.Create("StagingTable", p.Table.StagingTable), Tuple.Create("GuidTable", p.Table.GuidTable),
                                              Tuple.Create("PrimaryIdField", p.Table.PrimaryId) })
                {
                    var value = Get(entity, check.Item1);
                    if (Str(value).ToLowerInvariant() != check.Item2.ToLowerInvariant())
                        fail("PassNo 2 " + check.Item1 + " " + ValueRepr(value) + " differs from the manifest (" + Py.Repr(check.Item2) + ")");
                }
                var rows = seed["ColumnMap"].Where(r => Str(Get(r, "LogicalName")).ToLowerInvariant() == name.ToLowerInvariant() && IsTwo(Get(r, "PassNo"))).ToList();
                var byTarget = new List<KeyValuePair<string, Dictionary<string, object>>>();
                foreach (var r in rows)
                {
                    var t = Str(Get(r, "TargetAttribute")).ToLowerInvariant();
                    var i = byTarget.FindIndex(kv => kv.Key == t);
                    var entry = new KeyValuePair<string, Dictionary<string, object>>(t, r);
                    if (i >= 0) byTarget[i] = entry; else byTarget.Add(entry);
                }
                var key = byTarget.Where(kv => kv.Key == p.Key.Name.ToLowerInvariant()).Select(kv => kv.Value).FirstOrDefault();
                if (key == null || Str(Get(key, "StagingColumn")).ToLowerInvariant() != p.Key.Name.ToLowerInvariant() || !IsOne(Get(key, "IncludeOnUpdate")))
                    fail("PassNo 2 does not key the update on " + Py.Repr(p.Key.Name));
                var written = byTarget.Where(kv => IsOne(Get(kv.Value, "IncludeOnUpdate")) && !ReferenceEquals(kv.Value, key)).ToList();
                var expected = new HashSet<string>(p.Columns.Select(c => c.Name.ToLowerInvariant()), StringComparer.Ordinal);
                // The type column of a polymorphic lookup may or may not have its own PassNo 2 row.
                var typeNames = new HashSet<string>(p.TypeColumns.Select(c => c.Name.ToLowerInvariant()), StringComparer.Ordinal);
                if (!new HashSet<string>(written.Select(kv => kv.Key).Where(k => !typeNames.Contains(k)), StringComparer.Ordinal).SetEquals(expected))
                    fail("PassNo 2 update columns " + Py.Repr(Py.Sorted(written.Select(kv => kv.Key))) + " differ from the manifest's deferred columns "
                         + Py.Repr(Py.Sorted(expected)));
                foreach (var column in p.Columns)
                {
                    var row = written.First(kv => kv.Key == column.Name.ToLowerInvariant()).Value;
                    var target = Str(Get(row, "LookupTargetEntity")).ToLowerInvariant();
                    var polymorphic = column.LookupTargets.Count != 1 || column.IsPolymorphic;
                    bool targetOk;
                    if (target.Length == 0) targetOk = polymorphic;   // no single target entity; the scaffolder writes NULL
                    else if (polymorphic) targetOk = column.LookupTargets.Any(t => t.ToLowerInvariant() == target);
                    else targetOk = target == column.LookupTargets[0].ToLowerInvariant();
                    if (Str(Get(row, "StagingColumn")).ToLowerInvariant() != column.Name.ToLowerInvariant() || !IsOne(Get(row, "IsLookup")) || !targetOk)
                        fail("PassNo 2 mapping for " + Py.Repr(column.Name) + " disagrees with the manifest lookup " + Py.Repr(string.Join("/", column.LookupTargets)));
                }
                var unwritten = Py.Sorted(byTarget.Where(kv => !IsOne(Get(kv.Value, "IncludeOnUpdate"))).Select(kv => Str(Get(kv.Value, "TargetAttribute"))));
                var copy = p.Copy();
                copy.MetaEnabled = IsOne(Get(entity, "IsEnabled"));
                copy.MetaUnwritten = unwritten;
                checkedPasses.Add(copy);
            }
            return checkedPasses;
        }

        public static JObj ReportEntry(DeferredPass p, string packageName)
        {
            return new JObj()
                .Add("table", p.Table.LogicalName)
                .Add("package", packageName)
                .Add("file", packageName + ".dtsx")
                .Add("stagingTable", p.Table.StagingTable)
                .Add("keyColumn", p.Key.Name)
                .Add("columns", p.Columns.Select(c => (object)new JObj().Add("column", c.Name).Add("lookupTarget", string.Join(";", c.LookupTargets))).ToList())
                .Add("typeColumns", p.TypeColumns.Select(c => (object)c.Name).ToList())
                .Add("droppedTargets", p.DroppedTargets.Cast<object>().ToList())
                .Add("metaSeed", p.MetaEnabled == null ? null
                    : new JObj().Add("passNo", 2).Add("isEnabled", p.MetaEnabled.Value).Add("notWritten", p.MetaUnwritten.Cast<object>().ToList()));
        }
    }
}
