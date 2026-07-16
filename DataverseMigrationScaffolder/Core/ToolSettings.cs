using System;
using System.Collections.Generic;
using System.Linq;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>Checked-table selection (and solution choice) for one environment.</summary>
    public class OrgSelection
    {
        public string OrgKey { get; set; }
        public List<string> Tables { get; set; } = new List<string>();
        public string Solution { get; set; }   // last selected solution unique name
    }

    /// <summary>Persisted via XrmToolBox SettingsManager (XmlSerializer under the hood).</summary>
    public class ToolSettings
    {
        public string SchemaName { get; set; } = "dbo";
        public int BatchSize { get; set; } = 40;
        public string OutputFolder { get; set; } = "";
        public string StagingPrefix { get; set; } = "stage_";
        public string GuidPrefix { get; set; } = "guid_";

        /// <summary>Legacy (pre per-environment) selection; used as fallback for unknown orgs.</summary>
        public List<string> CheckedTables { get; set; } = new List<string>();

        /// <summary>Checked tables remembered per connected environment.</summary>
        public List<OrgSelection> OrgSelections { get; set; } = new List<OrgSelection>();

        public List<string> GetSelection(string orgKey)
        {
            var entry = OrgSelections == null
                ? null
                : OrgSelections.FirstOrDefault(o => string.Equals(o.OrgKey, orgKey, StringComparison.OrdinalIgnoreCase));
            if (entry != null) return entry.Tables ?? new List<string>();
            return CheckedTables ?? new List<string>();   // legacy fallback
        }

        public void SetSelection(string orgKey, List<string> tables)
        {
            if (OrgSelections == null) OrgSelections = new List<OrgSelection>();
            var entry = OrgSelections.FirstOrDefault(o => string.Equals(o.OrgKey, orgKey, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new OrgSelection { OrgKey = orgKey };
                OrgSelections.Add(entry);
            }
            entry.Tables = tables;
        }

        // ---- Output options -------------------------------------------------
        public bool GenerateStaging { get; set; } = true;
        public bool GenerateGuid { get; set; } = true;
        /// <summary>true = DROP TABLE IF EXISTS + CREATE; false = CREATE only if missing.</summary>
        public bool StagingDropRecreate { get; set; } = true;
        public bool GuidDropRecreate { get; set; } = false;
        /// <summary>Emit guarded nonclustered indexes on every match-key column.</summary>
        public bool IndexLegacyIdColumns { get; set; } = false;

        /// <summary>
        /// Comma-separated suffixes identifying "match key" columns (carried into guid tables
        /// and indexed by the index option). Default "legacyid" matches jn_legacyid,
        /// jnc_legacyid, new_legacyid, etc.
        /// </summary>
        public string MatchKeySuffixes { get; set; } = "legacyid";

        public bool IsMatchKey(string columnName)
        {
            if (string.IsNullOrEmpty(columnName) || string.IsNullOrEmpty(MatchKeySuffixes)) return false;
            foreach (var part in MatchKeySuffixes.Split(','))
            {
                var suffix = part.Trim();
                if (suffix.Length > 0 && columnName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        /// <summary>Emit truncate.sql truncating all staging tables (guid truncates commented out).</summary>
        public bool GenerateTruncateScript { get; set; } = false;
        /// <summary>Emit teardown.sql dropping all staging tables (guid drops commented out).</summary>
        public bool GenerateTeardown { get; set; } = false;
        /// <summary>Emit data_dictionary.xlsx: one sheet per table, ordered by display name.</summary>
        public bool GenerateDataDictionary { get; set; } = false;
        /// <summary>Emit diagram.mmd: Mermaid flowchart of lookup dependencies grouped by tier.</summary>
        public bool GenerateMermaid { get; set; } = false;

        /// <summary>Show managed solutions in the solution picker (patches are always excluded).</summary>
        public bool IncludeManagedSolutions { get; set; } = false;

        /// <summary>
        /// Field logical names excluded from dependency ranking on ALL tables
        /// (comma/newline separated). The columns are still emitted; their lookup
        /// targets just don't influence tier ordering.
        /// </summary>
        public string DependencyExclusions { get; set; } = "";

        public HashSet<string> GetDependencyExclusions()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(DependencyExclusions))
            {
                var parts = DependencyExclusions.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0) set.Add(trimmed);
                }
            }
            return set;
        }

        public string GetSolution(string orgKey)
        {
            var entry = OrgSelections == null
                ? null
                : OrgSelections.FirstOrDefault(o => string.Equals(o.OrgKey, orgKey, StringComparison.OrdinalIgnoreCase));
            return entry != null && !string.IsNullOrEmpty(entry.Solution) ? entry.Solution : "Default";
        }

        public void SetSolution(string orgKey, string solutionUniqueName)
        {
            if (OrgSelections == null) OrgSelections = new List<OrgSelection>();
            var entry = OrgSelections.FirstOrDefault(o => string.Equals(o.OrgKey, orgKey, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new OrgSelection { OrgKey = orgKey };
                OrgSelections.Add(entry);
            }
            entry.Solution = solutionUniqueName;
        }
    }
}
