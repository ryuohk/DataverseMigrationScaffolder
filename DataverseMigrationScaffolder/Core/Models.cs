using System;
using System.Collections.Generic;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>A column destined for a generated SQL table.</summary>
    public class SqlColumn
    {
        public string Name { get; set; }            // logical name, e.g. jn_caseid
        public string SqlType { get; set; }         // e.g. NVARCHAR(100), DATETIME2(7)
        public bool IsLookup { get; set; }
        public bool IsPolymorphic { get; set; }     // more than one target (customer/owner/regarding)
        public string[] Targets { get; set; }       // lookup target logical names
        public bool IsPrimaryId { get; set; }
        public bool IsPrimaryName { get; set; }
        public bool IsTypeCompanion { get; set; }   // companion "<lookup>type" column for polymorphic lookups

        // Data-dictionary enrichment (from metadata)
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string AttributeTypeName { get; set; } = "";
        public string AdditionalInfo { get; set; } = "";
        public bool IsCustomAttribute { get; set; }

        public SqlColumn() { }

        public SqlColumn(string name, string sqlType)
        {
            Name = name;
            SqlType = sqlType;
        }
    }

    /// <summary>A Dataverse table plus everything needed to emit its staging/guid DDL.</summary>
    public class TableModel
    {
        public string LogicalName { get; set; }
        public string SchemaName { get; set; }      // preserves casing, e.g. jn_Allegation
        public string DisplayName { get; set; }
        public string Prefix { get; set; }          // publisher prefix parsed from the logical name; "oob" if none
        public string PrimaryIdAttribute { get; set; }
        public string PrimaryNameAttribute { get; set; }

        // Data-dictionary enrichment (from metadata)
        public string PluralDisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public int? ObjectTypeCode { get; set; }
        public bool IsCustomEntity { get; set; }
        public string OwnershipType { get; set; } = "";
        public string IntroducedVersion { get; set; } = "";

        /// <summary>Columns derived from metadata (filtered, mapped, alphabetized).</summary>
        public List<SqlColumn> Columns { get; } = new List<SqlColumn>();

        /// <summary>Logical names of tables this table depends on (lookup targets), within the selected set.</summary>
        public HashSet<string> Dependencies { get; } = new HashSet<string>();
    }

    /// <summary>One generated .sql file.</summary>
    public class GeneratedFile
    {
        public string FileName { get; set; }
        public string Content { get; set; }
        public byte[] BinaryContent { get; set; }  // set for non-text outputs (xlsx); Content then holds a preview note
        public string Description { get; set; }    // shown next to the file name in the UI
        public List<string> Tables { get; } = new List<string>();
    }

    /// <summary>A Dataverse solution, for the solution picker.</summary>
    public class SolutionInfo
    {
        public Guid Id { get; set; }
        public string UniqueName { get; set; }
        public string FriendlyName { get; set; }
        public bool IsManaged { get; set; }

        public override string ToString()
        {
            var name = string.IsNullOrEmpty(FriendlyName) ? UniqueName : FriendlyName;
            return IsManaged ? name + " (managed)" : name;
        }
    }

    /// <summary>
    /// Component membership of a selected solution. Entities maps entity MetadataId to
    /// rootcomponentbehavior (0 = include subcomponents = all attributes; 1/2 = only the
    /// attributes explicitly listed in Attributes).
    /// </summary>
    public class SolutionFilter
    {
        public Dictionary<Guid, int> Entities { get; } = new Dictionary<Guid, int>();
        public HashSet<Guid> Attributes { get; } = new HashSet<Guid>();
    }

    /// <summary>Result of a generation run.</summary>
    public class GenerationResult
    {
        public List<GeneratedFile> Files { get; } = new List<GeneratedFile>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> OrderedTables { get; } = new List<string>();
    }
}
