using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace XrmDataversePlugin
{
    internal class ErdDataService
    {
        private readonly IOrganizationService _service;

        public ErdDataService(IOrganizationService service) => _service = service;

        // ── Solutions ────────────────────────────────────────────────────────
        public List<SolutionInfo> GetSolutions()
        {
            var q = new QueryExpression("solution")
            {
                ColumnSet = new ColumnSet("solutionid", "uniquename", "friendlyname")
            };
            q.Criteria.AddCondition("isvisible", ConditionOperator.Equal, true);
            q.AddOrder("friendlyname", OrderType.Ascending);

            return _service.RetrieveMultiple(q).Entities
                .Select(e => new SolutionInfo
                {
                    Id = e.GetAttributeValue<Guid>("solutionid"),
                    UniqueName = e.GetAttributeValue<string>("uniquename") ?? "",
                    FriendlyName = e.GetAttributeValue<string>("friendlyname") ?? e.GetAttributeValue<string>("uniquename") ?? ""
                })
                .ToList();
        }

        // ── Entities in solution ─────────────────────────────────────────────
        public List<EntityInfo> GetEntitiesInSolution(Guid solutionId)
        {
            var compQ = new QueryExpression("solutioncomponent")
            {
                ColumnSet = new ColumnSet("objectid")
            };
            compQ.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            compQ.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 1); // Entity

            var objectIds = _service.RetrieveMultiple(compQ).Entities
                .Select(e => e.GetAttributeValue<Guid>("objectid"))
                .ToHashSet();

            if (objectIds.Count == 0) return new List<EntityInfo>();

            var allResp = (RetrieveAllEntitiesResponse)_service.Execute(
                new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = false });

            return allResp.EntityMetadata
                .Where(m => objectIds.Contains(m.MetadataId ?? Guid.Empty))
                .Select(m => new EntityInfo
                {
                    LogicalName = m.LogicalName,
                    DisplayName = m.DisplayName?.UserLocalizedLabel?.Label ?? m.LogicalName,
                    MetadataId = m.MetadataId ?? Guid.Empty
                })
                .OrderBy(e => e.DisplayName)
                .ToList();
        }

        // ── Build ERD schema ─────────────────────────────────────────────────
        public ErdSchema BuildSchema(IEnumerable<string> entityLogicalNames)
        {
            var names = new HashSet<string>(entityLogicalNames, StringComparer.OrdinalIgnoreCase);
            var schema = new ErdSchema();
            var seenRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (names.Count == 0) return schema;

            // Batch all entity fetches into one ExecuteMultiple call (chunks of 50)
            var allMeta = new List<EntityMetadata>();
            var nameList = names.ToList();
            const int chunkSize = 50;
            for (int ci = 0; ci < nameList.Count; ci += chunkSize)
            {
                var chunk = nameList.Skip(ci).Take(chunkSize).ToList();
                var batch = new ExecuteMultipleRequest
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
                };
                foreach (var n in chunk)
                    batch.Requests.Add(new RetrieveEntityRequest
                    {
                        LogicalName = n,
                        EntityFilters = EntityFilters.Attributes | EntityFilters.Relationships
                    });
                var batchResp = (ExecuteMultipleResponse)_service.Execute(batch);
                allMeta.AddRange(batchResp.Responses
                    .Where(r => r.Fault == null && r.Response != null)
                    .Select(r => ((RetrieveEntityResponse)r.Response).EntityMetadata));
            }

            foreach (var meta in allMeta)
            {
                var name = meta.LogicalName;

                var table = new ErdTable
                {
                    Id = meta.LogicalName,
                    Name = meta.LogicalName,
                    DisplayName = meta.DisplayName?.UserLocalizedLabel?.Label ?? meta.LogicalName
                };

                // PK first, then FKs, then rest — sorted alphabetically within each group
                var attrs = meta.Attributes
                    .Where(a => a.AttributeOf == null && a.AttributeType != AttributeTypeCode.Virtual)
                    .OrderBy(a => a.IsPrimaryId == true ? 0 : IsLookup(a) ? 1 : 2)
                    .ThenBy(a => a.LogicalName);

                foreach (var attr in attrs)
                {
                    table.Columns.Add(new ErdColumn
                    {
                        Name = attr.LogicalName,
                        DisplayName = attr.DisplayName?.UserLocalizedLabel?.Label ?? attr.LogicalName,
                        Type = TypeLabel(attr),
                        IsPk = attr.IsPrimaryId == true,
                        IsFk = IsLookup(attr)
                    });
                }

                schema.Tables.Add(table);

                // 1:N where this entity is the "1" side
                foreach (var r in meta.OneToManyRelationships)
                {
                    if (!names.Contains(r.ReferencingEntity)) continue;
                    if (seenRels.Add(r.SchemaName))
                        schema.Relationships.Add(new ErdRelationship
                        {
                            Id = r.SchemaName,
                            FromTable = r.ReferencedEntity,
                            ToTable = r.ReferencingEntity,
                            Cardinality = "1-*",
                            Label = r.SchemaName,
                            ReferencingAttribute = r.ReferencingAttribute,
                            IsSystem = _sysAttrs.Contains(r.ReferencingAttribute ?? "")
                                    || _sysEntities.Contains(r.ReferencedEntity)
                                    || _sysEntities.Contains(r.ReferencingEntity)
                        });
                }

                // N:N — only add once
                foreach (var r in meta.ManyToManyRelationships)
                {
                    var other = r.Entity1LogicalName.Equals(name, StringComparison.OrdinalIgnoreCase)
                        ? r.Entity2LogicalName : r.Entity1LogicalName;
                    if (!names.Contains(other)) continue;
                    if (seenRels.Add(r.SchemaName))
                        schema.Relationships.Add(new ErdRelationship
                        {
                            Id = r.SchemaName,
                            FromTable = r.Entity1LogicalName,
                            ToTable = r.Entity2LogicalName,
                            Cardinality = "*-*",
                            Label = r.SchemaName,
                            IsSystem = _sysEntities.Contains(r.Entity1LogicalName)
                                    || _sysEntities.Contains(r.Entity2LogicalName)
                        });
                }
            }

            return schema;
        }

        private static readonly HashSet<string> _sysAttrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ownerid","owningteam","owninguser","owningbusinessunit",
            "createdby","modifiedby","createdonbehalfby","modifiedonbehalfby",
            "transactioncurrencyid","stageid","processid"
        };

        private static readonly HashSet<string> _sysEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "systemuser","team","businessunit","transactioncurrency",
            "queue","calendar","organization"
        };

        private static bool IsLookup(AttributeMetadata a) =>
            a.AttributeType == AttributeTypeCode.Lookup ||
            a.AttributeType == AttributeTypeCode.Customer ||
            a.AttributeType == AttributeTypeCode.Owner;

        private static string TypeLabel(AttributeMetadata a) => a.AttributeType switch
        {
            AttributeTypeCode.String => "Text",
            AttributeTypeCode.Memo => "Memo",
            AttributeTypeCode.Integer => "Int",
            AttributeTypeCode.BigInt => "BigInt",
            AttributeTypeCode.Decimal => "Decimal",
            AttributeTypeCode.Double => "Float",
            AttributeTypeCode.Money => "Currency",
            AttributeTypeCode.Boolean => "Yes/No",
            AttributeTypeCode.DateTime => "DateTime",
            AttributeTypeCode.Lookup => "Lookup",
            AttributeTypeCode.Customer => "Customer",
            AttributeTypeCode.Owner => "Owner",
            AttributeTypeCode.Picklist => "Choice",
            AttributeTypeCode.State => "State",
            AttributeTypeCode.Status => "Status",
            AttributeTypeCode.Uniqueidentifier => "GUID",
            AttributeTypeCode.EntityName => "EntityName",
            _ => a.AttributeType?.ToString() ?? "?"
        };

        // ── JSON serialisation (no external deps) ───────────────────────────
        public static string ToJson(ErdSchema schema)
        {
            var sb = new StringBuilder();
            sb.Append("{\"tables\":[");
            for (int ti = 0; ti < schema.Tables.Count; ti++)
            {
                if (ti > 0) sb.Append(',');
                var t = schema.Tables[ti];
                sb.Append($"{{\"id\":{Js(t.Id)},\"name\":{Js(t.Name)},\"displayName\":{Js(t.DisplayName)},\"columns\":[");
                for (int ci = 0; ci < t.Columns.Count; ci++)
                {
                    if (ci > 0) sb.Append(',');
                    var c = t.Columns[ci];
                    sb.Append($"{{\"name\":{Js(c.Name)},\"displayName\":{Js(c.DisplayName)},\"type\":{Js(c.Type)},\"pk\":{B(c.IsPk)},\"fk\":{B(c.IsFk)}}}");
                }
                sb.Append("]}");
            }
            sb.Append("],\"relationships\":[");
            for (int ri = 0; ri < schema.Relationships.Count; ri++)
            {
                if (ri > 0) sb.Append(',');
                var r = schema.Relationships[ri];
                sb.Append($"{{\"id\":{Js(r.Id)},\"fromTable\":{Js(r.FromTable)},\"toTable\":{Js(r.ToTable)},\"cardinality\":{Js(r.Cardinality)},\"label\":{Js(r.Label)},\"referencingAttribute\":{Js(r.ReferencingAttribute)},\"isSystem\":{B(r.IsSystem)}}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Js(string? s) => s == null ? "null"
            : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

        private static string B(bool b) => b ? "true" : "false";
    }

    internal class SolutionInfo
    {
        public Guid Id { get; set; }
        public string UniqueName { get; set; } = "";
        public string FriendlyName { get; set; } = "";
        public override string ToString() => FriendlyName;
    }

    internal class EntityInfo
    {
        public string LogicalName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public Guid MetadataId { get; set; }
        public override string ToString() => $"{DisplayName} ({LogicalName})";
    }

    internal class ErdSchema
    {
        public List<ErdTable> Tables { get; } = new();
        public List<ErdRelationship> Relationships { get; } = new();
    }

    internal class ErdTable
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public List<ErdColumn> Columns { get; } = new();
    }

    internal class ErdColumn
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsPk { get; set; }
        public bool IsFk { get; set; }
    }

    internal class ErdRelationship
    {
        public string Id { get; set; } = "";
        public string FromTable { get; set; } = "";
        public string ToTable { get; set; } = "";
        public string Cardinality { get; set; } = "1-*";
        public string Label { get; set; } = "";
        public string? ReferencingAttribute { get; set; }
        public bool IsSystem { get; set; }
    }
}
