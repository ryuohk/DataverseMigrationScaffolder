using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>
    /// Converts Dataverse AttributeMetadata into SqlColumn definitions using the
    /// harness conventions:
    ///   lookups / choices / money / decimal -> NVARCHAR(100)
    ///   string  -> NVARCHAR(MaxLength, MAX above 4000), memo -> NVARCHAR(MAX)
    ///   whole number -> INT, biginteger -> BIGINT, boolean -> BIT
    ///   datetime -> DATETIME2(7), date-only -> DATE
    ///   money also emits a companion _base column
    ///   polymorphic lookups emit a companion &lt;name&gt;type column
    /// </summary>
    public static class MetadataMapper
    {
        /// <summary>System columns never taken from metadata (some are re-added as fixed boilerplate).</summary>
        private static readonly HashSet<string> GlobalSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "createdon", "createdby", "modifiedon", "modifiedby",
            "createdonbehalfby", "modifiedonbehalfby",
            "owningbusinessunit", "owningteam", "owninguser", "ownerid",
            "statecode", "statuscode", "overriddencreatedon",
            "importsequencenumber", "timezoneruleversionnumber", "utcconversiontimezonecode",
            "versionnumber", "processid", "stageid", "traversedpath"
        };

        public static TableModel BuildTable(EntityMetadata entity, ToolSettings settings, SolutionFilter solutionFilter)
        {
            var table = new TableModel
            {
                LogicalName = entity.LogicalName,
                SchemaName = entity.SchemaName,
                DisplayName = Lbl(entity.DisplayName, entity.LogicalName),
                PluralDisplayName = Lbl(entity.DisplayCollectionName, ""),
                Description = Lbl(entity.Description, ""),
                Prefix = MetadataService.GetPrefix(entity.LogicalName),
                PrimaryIdAttribute = entity.PrimaryIdAttribute,
                PrimaryNameAttribute = entity.PrimaryNameAttribute,
                ObjectTypeCode = entity.ObjectTypeCode,
                IsCustomEntity = entity.IsCustomEntity.GetValueOrDefault(),
                OwnershipType = entity.OwnershipType.HasValue ? entity.OwnershipType.Value.ToString() : "",
                IntroducedVersion = entity.IntroducedVersion ?? ""
            };

            var columns = new List<SqlColumn>();

            foreach (var attr in entity.Attributes.OrderBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase))
            {
                var col = MapAttribute(attr, entity, solutionFilter);
                if (col == null) continue;
                columns.Add(col);

                // Money columns carry a companion _base column (base currency value).
                if (IsMoney(attr))
                {
                    columns.Add(new SqlColumn(col.Name + "_base", "NVARCHAR(100)")
                    {
                        DisplayName = col.DisplayName + " (Base)",
                        AttributeTypeName = "Money",
                        AdditionalInfo = "generated base-currency companion",
                        IsCustomAttribute = col.IsCustomAttribute
                    });
                }

                // Polymorphic lookups carry a companion "<name>type" column (e.g. jn_regardingidtype).
                if (col.IsLookup && col.IsPolymorphic)
                {
                    columns.Add(new SqlColumn(col.Name + "type", "NVARCHAR(100)")
                    {
                        IsTypeCompanion = true,
                        AttributeTypeName = "EntityName",
                        AdditionalInfo = "generated target-table companion for " + col.Name,
                        IsCustomAttribute = col.IsCustomAttribute
                    });
                }
            }

            // Alphabetical order for the metadata-derived block (matches existing harness scripts).
            foreach (var col in columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                table.Columns.Add(col);
            }

            // Dependencies for topological ordering (self references ignored). All lookups
            // contribute, including polymorphic ones (every target counts as a dependency).
            // User-defined exclusions (settings) are skipped - use them to tame regarding-style
            // lookups that would otherwise create excessive cycles.
            var exclusions = settings.GetDependencyExclusions();
            foreach (var col in table.Columns.Where(c => c.IsLookup && c.Targets != null))
            {
                if (exclusions.Contains(col.Name)) continue;
                foreach (var target in col.Targets)
                {
                    if (!string.Equals(target, entity.LogicalName, StringComparison.OrdinalIgnoreCase))
                    {
                        table.Dependencies.Add(target.ToLowerInvariant());
                    }
                }
            }

            return table;
        }

        private static string Lbl(Label label, string fallback)
        {
            return label != null && label.UserLocalizedLabel != null && !string.IsNullOrEmpty(label.UserLocalizedLabel.Label)
                ? label.UserLocalizedLabel.Label
                : fallback;
        }

        private static SqlColumn MapAttribute(AttributeMetadata attr, EntityMetadata entity, SolutionFilter solutionFilter)
        {
            var name = attr.LogicalName;

            if (GlobalSkip.Contains(name)) return null;

            // Skip virtual helper attributes (picklist "name" columns, yomi, composite, etc.)
            if (attr.AttributeOf != null) return null;

            // Money _base columns are emitted alongside their parent money column instead.
            if (name.EndsWith("_base", StringComparison.OrdinalIgnoreCase)) return null;

            var isCustom = attr.IsCustomAttribute.GetValueOrDefault();
            var isPrimaryId = string.Equals(name, entity.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase);
            var isPrimaryName = string.Equals(name, entity.PrimaryNameAttribute, StringComparison.OrdinalIgnoreCase);

            // Solution filtering: when a specific (non-Default) solution is selected, an
            // attribute is included only if the solution contains it - either the entity was
            // added with subcomponents (rootcomponentbehavior 0) or the attribute was added
            // explicitly. Primary id and primary name are always kept (the guid tables need them).
            if (solutionFilter != null && !isPrimaryId && !isPrimaryName)
            {
                var behavior = 1;   // "not included with subcomponents" unless we know better
                if (entity.MetadataId.HasValue)
                {
                    int known;
                    if (solutionFilter.Entities.TryGetValue(entity.MetadataId.Value, out known)) behavior = known;
                }

                if (behavior != 0 &&
                    (!attr.MetadataId.HasValue || !solutionFilter.Attributes.Contains(attr.MetadataId.Value)))
                {
                    return null;
                }
            }

            SqlColumn col = null;
            string extra = "";

            switch (attr.AttributeType.GetValueOrDefault())
            {
                case AttributeTypeCode.String:
                    var s = attr as StringAttributeMetadata;
                    var len = s != null && s.MaxLength.HasValue ? s.MaxLength.Value : 100;
                    // NVARCHAR(n) caps at 4000; larger declared lengths (e.g. annotation.documentbody
                    // reports ~1GB) must be NVARCHAR(MAX).
                    var sqlLen = (len > 4000 || len < 1) ? "MAX" : len.ToString();
                    extra = "Max length: " + len;
                    col = new SqlColumn(name, "NVARCHAR(" + sqlLen + ")") { IsPrimaryName = isPrimaryName };
                    break;

                case AttributeTypeCode.Memo:
                    var memo = attr as MemoAttributeMetadata;
                    if (memo != null && memo.MaxLength.HasValue) extra = "Max length: " + memo.MaxLength.Value;
                    col = new SqlColumn(name, "NVARCHAR(MAX)");
                    break;

                case AttributeTypeCode.Integer:
                    col = new SqlColumn(name, "INT");
                    break;

                case AttributeTypeCode.BigInt:
                    col = new SqlColumn(name, "BIGINT");
                    break;

                case AttributeTypeCode.Boolean:
                    col = new SqlColumn(name, "BIT");
                    break;

                case AttributeTypeCode.Decimal:
                case AttributeTypeCode.Double:
                case AttributeTypeCode.Money:
                case AttributeTypeCode.Picklist:
                    col = new SqlColumn(name, "NVARCHAR(100)");
                    break;

                case AttributeTypeCode.DateTime:
                    var dt = attr as DateTimeAttributeMetadata;
                    var dateOnly = dt != null && dt.Format.HasValue && dt.Format.Value == DateTimeFormat.DateOnly;
                    extra = dateOnly ? "Format: DateOnly" : "Format: DateAndTime";
                    col = new SqlColumn(name, dateOnly ? "DATE" : "DATETIME2(7)");
                    break;

                case AttributeTypeCode.Lookup:
                case AttributeTypeCode.Customer:
                case AttributeTypeCode.Owner:
                    var lookup = attr as LookupAttributeMetadata;
                    var targets = lookup != null && lookup.Targets != null ? lookup.Targets : new string[0];
                    col = new SqlColumn(name, "NVARCHAR(100)")
                    {
                        IsLookup = true,
                        Targets = targets,
                        IsPolymorphic = targets.Length != 1
                    };
                    break;

                case AttributeTypeCode.Uniqueidentifier:
                    // Only the table's own primary key (e.g. jn_allegationid); skip address1_addressid etc.
                    if (isPrimaryId) col = new SqlColumn(name, "NVARCHAR(100)") { IsPrimaryId = true };
                    break;

                case AttributeTypeCode.Virtual:
                    // Multi-select choices come through as Virtual with MultiSelectPicklist metadata.
                    if (attr is MultiSelectPicklistAttributeMetadata)
                    {
                        col = new SqlColumn(name, "NVARCHAR(MAX)");
                    }
                    break;

                default:
                    // State/Status handled by skip list; PartyList, EntityName, ManagedProperty, Image, File -> skip.
                    break;
            }

            if (col == null) return null;

            col.DisplayName = Lbl(attr.DisplayName, "");
            col.Description = Lbl(attr.Description, "");
            col.AttributeTypeName = attr.AttributeType.GetValueOrDefault().ToString();
            col.AdditionalInfo = extra;
            col.IsCustomAttribute = isCustom;
            return col;
        }

        /// <summary>True when the attribute is a money column that needs a companion _base column.</summary>
        public static bool IsMoney(AttributeMetadata attr)
        {
            return attr.AttributeType.GetValueOrDefault() == AttributeTypeCode.Money;
        }
    }
}
