using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>Thin wrapper over the Dataverse metadata messages.</summary>
    public class MetadataService
    {
        private readonly IOrganizationService _service;

        public MetadataService(IOrganizationService service)
        {
            _service = service;
        }

        /// <summary>Fast list of tables (no attributes) for the picker grid.</summary>
        public List<EntityMetadata> GetAllTables()
        {
            var request = new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            };
            var response = (RetrieveAllEntitiesResponse)_service.Execute(request);
            return response.EntityMetadata
                .Where(e => e.IsIntersect != true)
                .OrderBy(e => e.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Full attribute metadata for one table.</summary>
        public EntityMetadata GetTableWithAttributes(string logicalName)
        {
            var request = new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                // Entity flag included so entity-level info (plural name, description,
                // ownership, OTC) is populated for the data dictionary.
                EntityFilters = EntityFilters.Entity | EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            };
            var response = (RetrieveEntityResponse)_service.Execute(request);
            return response.EntityMetadata;
        }

        /// <summary>
        /// Visible solutions (patches always excluded), "Default" first, the rest alphabetical.
        /// Managed solutions are included only when requested.
        /// </summary>
        public List<SolutionInfo> GetSolutions(bool includeManaged)
        {
            var query = new QueryExpression("solution")
            {
                ColumnSet = new ColumnSet("uniquename", "friendlyname", "solutionid", "ismanaged")
            };
            query.Criteria.AddCondition("isvisible", ConditionOperator.Equal, true);
            if (!includeManaged)
            {
                query.Criteria.AddCondition("ismanaged", ConditionOperator.Equal, false);
            }
            query.Criteria.AddCondition("parentsolutionid", ConditionOperator.Null);   // excludes patches

            var result = _service.RetrieveMultiple(query);
            return result.Entities
                .Select(e => new SolutionInfo
                {
                    Id = e.Id,
                    UniqueName = e.GetAttributeValue<string>("uniquename") ?? "",
                    FriendlyName = e.GetAttributeValue<string>("friendlyname") ?? "",
                    IsManaged = e.GetAttributeValue<bool>("ismanaged")
                })
                .OrderBy(s => string.Equals(s.UniqueName, "Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(s => s.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Entity and attribute membership for one solution (componenttype 1 = entity,
        /// 2 = attribute), paged through solutioncomponent.
        /// </summary>
        public SolutionFilter GetSolutionFilter(Guid solutionId)
        {
            var filter = new SolutionFilter();

            var query = new QueryExpression("solutioncomponent")
            {
                ColumnSet = new ColumnSet("objectid", "componenttype", "rootcomponentbehavior"),
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };
            query.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            query.Criteria.AddCondition("componenttype", ConditionOperator.In, 1, 2);

            while (true)
            {
                var page = _service.RetrieveMultiple(query);
                foreach (var component in page.Entities)
                {
                    var objectId = component.GetAttributeValue<Guid>("objectid");
                    var type = component.GetAttributeValue<OptionSetValue>("componenttype");
                    if (type == null) continue;

                    if (type.Value == 1)
                    {
                        var behavior = component.GetAttributeValue<OptionSetValue>("rootcomponentbehavior");
                        filter.Entities[objectId] = behavior != null ? behavior.Value : 0;
                    }
                    else
                    {
                        filter.Attributes.Add(objectId);
                    }
                }

                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }

            return filter;
        }

        /// <summary>
        /// Category label = publisher prefix parsed from the logical name (text before the
        /// first underscore); tables without a prefix (contact, account, annotation, ...)
        /// are labelled "oob".
        /// </summary>
        public static string GetPrefix(string logicalName)
        {
            var idx = logicalName.IndexOf('_');
            return idx > 0 ? logicalName.Substring(0, idx) : "oob";
        }
    }
}
