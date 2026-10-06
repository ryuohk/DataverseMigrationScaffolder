using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>
    /// Describe the reference table from the reference package itself. The package holds one
    /// hand-built "Migrate &lt;Table&gt;" data flow:
    ///
    ///     OLE DB Source (stage_&lt;table&gt;) -> Conditional Split (record id is null?)
    ///         Create -> KingswaySoft destination (create) -> Default Output -> OLE DB Destination (guid_&lt;table&gt;)
    ///         Update -> KingswaySoft destination (update) [-> Default Output -> OLE DB Command (GUID table sync)]
    ///     Each KingswaySoft destination's Error Output -> OLE DB Destination ([dbo].[Error])
    ///
    /// Everything the generator substitutes (entity, staging and GUID tables, record id, legacy
    /// match key, staging columns and their types) is read from that data flow.
    /// </summary>
    internal static class Reference
    {
        public const string TaskPrefix = "Migrate ";
        public const string SavedRecordId = "SavedRecordId";
        /// <summary>Columns the scaffolder adds to every staging table (not manifest Dataverse fields).</summary>
        public static readonly HashSet<string> StagingBoilerplate =
            new HashSet<string>(new[] { "overriddencreatedon", "ownerid", "owneridtype", "statecode" }, StringComparer.Ordinal);

        private static SsisType TypeOf(XmlElement col)
        {
            Func<string, int?> num = a => col.GetAttribute(a).Length > 0 ? int.Parse(col.GetAttribute(a), CultureInfo.InvariantCulture) : (int?)null;
            return new SsisType(col.GetAttribute("dataType"), num("length"), num("precision"), num("scale"), num("codePage"));
        }

        /// <summary>(reference table, data flow task name).</summary>
        public static Tuple<Table, string> Infer(byte[] xml, string label, string stagingPrefix = "stage_", Table manifestTable = null)
        {
            Action<string> fail = m => { throw new GeneratorException("Reference package " + label + ": " + m); };
            XmlElement root;
            try { root = Xml.Parse(xml).DocumentElement; }
            catch (XmlException ex) { throw new GeneratorException("Reference package " + label + ": not well-formed XML: " + ex.Message, ex); }
            var tasks = Xml.Children(root, "Executables").SelectMany(c => Xml.Children(c, "Executable")).ToList();
            if (tasks.Count != 1 || tasks[0].GetAttribute("DTS:ExecutableType") != "Microsoft.Pipeline")
            {
                var names = string.Join(", ", tasks.Select(t => Py.Repr(t.GetAttribute("DTS:ObjectName"))));
                fail("expected exactly one data flow task (the table's Migrate flow), found: " + (names.Length > 0 ? names : "none"));
            }
            var task = tasks[0];
            var taskName = task.GetAttribute("DTS:ObjectName");
            var components = Xml.ByTag(task, "component");

            var entities = Py.Sorted(components.Select(Xml.Props).Where(p => p.Has("DestinationEntity")).Select(p => p.Text("DestinationEntity")).Distinct());
            if (entities.Count != 1)
                fail("expected KingswaySoft destinations for one entity, found " + (entities.Count > 0 ? Py.Repr(entities) : "none"));
            var entity = entities[0];

            var sources = components.Where(c => c.GetAttribute("componentClassID").ToLowerInvariant().Contains("oledbsource")).ToList();
            if (sources.Count != 1) fail("expected one OLE DB source reading the staging table, found " + sources.Count);
            var props = Xml.Props(sources[0]);
            var mode = props.Has("AccessMode") ? props.Text("AccessMode") : "0";
            string staging;
            if (mode == "0") staging = props["OpenRowset"] != null ? props.Text("OpenRowset") : "";
            else
            {
                var match = props.Has("SqlCommand") ? Package.Select.Match(props.Text("SqlCommand")) : null;
                staging = match != null && match.Success ? match.Groups[2].Value.TrimEnd(';') : "";
            }
            if (staging.Length == 0) fail("the OLE DB source does not name its staging table");
            var output = Xml.ByTag(sources[0], "output").FirstOrDefault(o => o.GetAttribute("isErrorOut") != "true");
            var sourceColumns = output != null ? Xml.ByTag(output, "outputColumn") : new List<XmlElement>();
            if (sourceColumns.Count == 0) fail("the OLE DB source has no output columns");

            // The create destination's Default Output feeds the OLE DB destination that fills the
            // GUID table. The update destination's Default Output may also feed an OLE DB Command
            // that keeps the GUID table row in sync (checked by Package.ValidateTemplate).
            XmlElement guidDest = null;
            var savedSuffix = ".Columns[" + SavedRecordId + "]";
            foreach (var component in components)
            {
                if (!Xml.ByTag(component, "inputColumn").Any(i => i.GetAttribute("lineageId").EndsWith(savedSuffix, StringComparison.Ordinal))) continue;
                var cls = component.GetAttribute("componentClassID").ToLowerInvariant();
                if (cls.Contains("oledbcommand")) continue;
                if (cls.Contains("oledbdestination") && guidDest == null) guidDest = component;
                else fail("more than one component consumes " + SavedRecordId + "; only one OLE DB destination "
                          + "(the GUID table) and OLE DB Commands that update it are supported");
            }
            if (guidDest == null)
                fail("no OLE DB destination writes the created records' " + SavedRecordId + " to the GUID table; "
                     + "connect the create destination's Default Output to guid_<table>");
            var guidProps = Xml.Props(guidDest);
            var guidTable = guidProps["OpenRowset"] != null ? guidProps.Text("OpenRowset") : "";
            if (guidTable.Length == 0) fail("the GUID table destination does not name its table");
            var extById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in Xml.ByTag(guidDest, "externalMetadataColumn")) extById[e.GetAttribute("refId")] = e.GetAttribute("name");
            string primaryId = null;
            var mapped = new List<string>();
            foreach (var inp in Xml.ByTag(guidDest, "inputColumn"))
            {
                string targetName;
                extById.TryGetValue(inp.GetAttribute("externalMetadataColumnId"), out targetName);
                if (inp.GetAttribute("lineageId").EndsWith(savedSuffix, StringComparison.Ordinal)) primaryId = targetName;
                else if (!string.IsNullOrEmpty(targetName)) mapped.Add(targetName);
            }
            if (string.IsNullOrEmpty(primaryId) || mapped.Count == 0)
                fail("the GUID table must receive " + SavedRecordId + " as the record id plus the legacy key column");
            var key = LegacyKey(mapped, manifestTable);
            if (key == null)
                fail("cannot tell which GUID table column is the legacy key (found " + Py.Repr(mapped) + "); name it *legacyid "
                     + "or add the reference table, with its match key, to the manifest");

            var names2 = sourceColumns.Select(c => c.GetAttribute("name")).ToList();
            var lowerNames = new HashSet<string>(names2.Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);
            foreach (var needed in new[] { primaryId }.Concat(mapped))
                if (!lowerNames.Contains(needed.ToLowerInvariant())) fail("staging source does not read " + Py.Repr(needed));
            var columns = sourceColumns.Select(c => new Column
            {
                Name = c.GetAttribute("name"), SqlType = "", DataverseType = "", SsisType = TypeOf(c),
                IsPrimaryId = c.GetAttribute("name").ToLowerInvariant() == primaryId.ToLowerInvariant(),
            }).ToList();

            var display = taskName.StartsWith(TaskPrefix, StringComparison.Ordinal) ? taskName.Substring(TaskPrefix.Length) : taskName;
            var schema = Metadata.ObjectName(staging);
            if (schema.ToLowerInvariant().StartsWith(stagingPrefix.ToLowerInvariant(), StringComparison.Ordinal)) schema = schema.Substring(stagingPrefix.Length);
            string primaryName = null;
            if (manifestTable != null && !string.IsNullOrEmpty(manifestTable.PrimaryName) && lowerNames.Contains(manifestTable.PrimaryName.ToLowerInvariant()))
                primaryName = manifestTable.PrimaryName;
            else
            {
                // The scaffolder's GUID table holds the record id, primary name, legacy key and
                // lookups; a single other entity column written to it is the primary name.
                var others = mapped.Where(m => m != key && !StagingBoilerplate.Contains(m.ToLowerInvariant())).ToList();
                if (others.Count == 1) primaryName = others[0];
            }
            if (!Py.Full("[A-Za-z0-9_]+").IsMatch(entity)) fail("unexpected DestinationEntity " + Py.Repr(entity));
            var table = new Table
            {
                LogicalName = entity, SchemaName = schema, DisplayName = display, Tier = 0, StagingTable = staging,
                GuidTable = guidTable, PrimaryId = primaryId, PrimaryName = primaryName, MatchKeys = new List<string> { key },
                Columns = columns,
            };
            return Tuple.Create(table, taskName);
        }

        /// <summary>The GUID table column the create branch writes as the legacy (match) key.</summary>
        private static string LegacyKey(List<string> mapped, Table manifestTable)
        {
            if (mapped.Count == 1) return mapped[0];
            if (manifestTable != null && manifestTable.MatchKeys.Count == 1)
            {
                var found = mapped.Where(m => m.ToLowerInvariant() == manifestTable.MatchKeys[0].ToLowerInvariant()).ToList();
                if (found.Count > 0) return found[0];
            }
            var legacy = mapped.Where(m => m.ToLowerInvariant().EndsWith("legacyid", StringComparison.Ordinal)).ToList();
            return legacy.Count == 1 ? legacy[0] : null;
        }
    }
}
