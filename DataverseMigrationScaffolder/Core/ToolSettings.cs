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

    /// <summary>What Export saves to the output folder.</summary>
    public enum ExportKind
    {
        /// <summary>NN_create_staging.sql and NN_create_guid.sql.</summary>
        SqlScripts,
        /// <summary>data_dictionary.xlsx.</summary>
        DataDictionary,
        /// <summary>Everything the SSIS generator reads: scripts, manifest.json and meta_seed.sql.</summary>
        ScaffolderRun,
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
        // Which files one ScriptGenerator run produces. The UI no longer edits these: every run
        // uses a copy from ForHarness or ForExport.
        public bool GenerateStaging { get; set; } = true;
        public bool GenerateGuid { get; set; } = true;
        /// <summary>true = DROP TABLE IF EXISTS + CREATE; false = CREATE only if missing.</summary>
        public bool StagingDropRecreate { get; set; } = true;
        public bool GuidDropRecreate { get; set; } = false;

        /// <summary>
        /// Comma-separated suffixes identifying "match key" columns (carried into guid tables
        /// and made UNIQUE in staging and GUID tables). Default "legacyid" matches new_legacyid,
        /// contoso_legacyid, etc.
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
        /// <summary>Emit data_dictionary.xlsx: one sheet per table, ordered by display name.</summary>
        public bool GenerateDataDictionary { get; set; } = false;
        /// <summary>Emit manifest.json: machine-readable run manifest (tables, tiers, files, columns, lookups, cycles).</summary>
        public bool GenerateJsonManifest { get; set; } = true;

        /// <summary>
        /// Emit meta_seed.sql: MERGE statements populating meta.Entity and meta.ColumnMap,
        /// the metadata an SSIS package generator reads to build the migration harness.
        /// Cycle members get a second PassNo = 2 row carrying only their deferred lookups.
        /// </summary>
        public bool GenerateMetadataSeed { get; set; } = false;

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

        // ---- SSIS project generation ------------------------------------------
        /// <summary>Reference SSIS solution (.sln) or project (.dtproj) the harness is cloned from.</summary>
        public string HarnessReference { get; set; } = "";
        /// <summary>The .dtproj inside HarnessReference (the reference itself when it is a .dtproj).</summary>
        public string HarnessProjectFile { get; set; } = "";
        /// <summary>Template package in that project, e.g. "01b - Harness.dtsx".</summary>
        public string HarnessPackage { get; set; } = "";
        public string HarnessProjectName { get; set; } = "MigrationHarness_Generated";
        /// <summary>Generate with protection level DontSaveSensitive: no passwords or secrets are stored
        /// in the project, so anyone can open it; credentials are supplied when deploying. Off = keep the
        /// reference's protection level and copy its encrypted values unchanged.</summary>
        public bool HarnessDontSaveSensitive { get; set; } = false;

        /// <summary>A copy that produces every output the SSIS generator reads (scripts, manifest, meta
        /// seed) whatever the user chose to save; prefixes, modes and match keys are kept.</summary>
        public ToolSettings ForHarness()
        {
            var copy = (ToolSettings)MemberwiseClone();
            copy.GenerateStaging = true;
            copy.GenerateGuid = true;
            copy.GenerateJsonManifest = true;
            copy.GenerateMetadataSeed = true;
            copy.GenerateDataDictionary = false;
            return copy;
        }

        /// <summary>A copy that produces exactly the files one Export choice saves.</summary>
        public ToolSettings ForExport(ExportKind kind)
        {
            var copy = (ToolSettings)MemberwiseClone();
            copy.GenerateStaging = copy.GenerateGuid = kind != ExportKind.DataDictionary;
            copy.GenerateJsonManifest = copy.GenerateMetadataSeed = kind == ExportKind.ScaffolderRun;
            copy.GenerateDataDictionary = kind == ExportKind.DataDictionary;
            return copy;
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
