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
    internal enum OptionSetScope
    {
        GlobalInSolution = 0,
        GlobalNotInSolution = 1,
        Local = 2
    }

    internal class OptionSetLoadResult
    {
        public List<OptionSetSummary> Summaries { get; set; } = new();
        public Dictionary<string, List<OptionSetUsage>> UsageMap { get; set; } = new();
    }

    internal class OptionSetService
    {
        private readonly IOrganizationService _service;

        public OptionSetService(IOrganizationService service) => _service = service;

        // ── Full tab load: solution globals + non-solution globals + locals ────
        // 3 API calls total; stores InlineMetadata so per-click needs 0 calls
        public OptionSetLoadResult GetAllOptionSetsForTab(Guid solutionId)
        {
            var result = new OptionSetLoadResult();

            // Step 1: combined solutioncomponent query for entity (1) and option set (9) IDs
            var compQ = new QueryExpression("solutioncomponent")
            {
                ColumnSet = new ColumnSet("objectid", "componenttype")
            };
            compQ.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            compQ.Criteria.AddCondition("componenttype", ConditionOperator.In, new object[] { 1, 9 });

            var entityIds = new List<Guid>();
            var solutionOsIds = new HashSet<Guid>();

            foreach (var e in _service.RetrieveMultiple(compQ).Entities)
            {
                var id = e.GetAttributeValue<Guid>("objectid");
                var type = e.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
                if (type == 1) entityIds.Add(id);
                else if (type == 9) solutionOsIds.Add(id);
            }

            // Step 2: entity metadata to extract all attribute option sets
            var entityMeta = new List<EntityMetadata>();
            if (entityIds.Count > 0)
            {
                const int chunk = 50;
                for (int i = 0; i < entityIds.Count; i += chunk)
                {
                    var slice = entityIds.Skip(i).Take(chunk).ToList();
                    var batch = new ExecuteMultipleRequest
                    {
                        Requests = new OrganizationRequestCollection(),
                        Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
                    };
                    foreach (var id in slice)
                        batch.Requests.Add(new RetrieveEntityRequest
                        {
                            MetadataId = id,
                            EntityFilters = EntityFilters.Attributes
                        });
                    var resp = (ExecuteMultipleResponse)_service.Execute(batch);
                    entityMeta.AddRange(resp.Responses
                        .Where(r => r.Fault == null && r.Response != null)
                        .Select(r => ((RetrieveEntityResponse)r.Response).EntityMetadata));
                }
            }

            // Step 3: solution global option sets (catches any not used by solution entities)
            var solutionGlobals = new List<OptionSetMetadata>();
            if (solutionOsIds.Count > 0)
            {
                var allOsResp = (RetrieveAllOptionSetsResponse)_service.Execute(
                    new RetrieveAllOptionSetsRequest { RetrieveAsIfPublished = false });
                solutionGlobals = allOsResp.OptionSetMetadata
                    .Where(os => solutionOsIds.Contains(os.MetadataId ?? Guid.Empty))
                    .OfType<OptionSetMetadata>()
                    .ToList();
            }

            // Build usage map from entity metadata
            result.UsageMap = BuildUsageMap(entityMeta);

            // Collect unique option sets seen in entity attributes
            var seen = new Dictionary<string, OptionSetSummary>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in entityMeta)
            {
                foreach (var attr in entity.Attributes)
                {
                    if (!(attr is PicklistAttributeMetadata pl)) continue;
                    if (pl.OptionSet == null) continue;

                    var name = pl.OptionSet.Name ?? "";
                    if (string.IsNullOrEmpty(name) || seen.ContainsKey(name)) continue;

                    var isGlobal = pl.OptionSet.IsGlobal == true;
                    var inSolution = isGlobal && solutionOsIds.Contains(pl.OptionSet.MetadataId ?? Guid.Empty);
                    var scope = !isGlobal ? OptionSetScope.Local
                              : inSolution ? OptionSetScope.GlobalInSolution
                              : OptionSetScope.GlobalNotInSolution;

                    seen[name] = new OptionSetSummary
                    {
                        Name = name,
                        DisplayName = pl.OptionSet.DisplayName?.UserLocalizedLabel?.Label ?? name,
                        MetadataId = pl.OptionSet.MetadataId ?? Guid.Empty,
                        Scope = scope,
                        InlineMetadata = pl.OptionSet
                    };
                }
            }

            // Add solution globals that aren't used by any solution entity (rare but possible)
            foreach (var osm in solutionGlobals)
            {
                var name = osm.Name ?? "";
                if (string.IsNullOrEmpty(name)) continue;
                if (seen.TryGetValue(name, out var existing))
                {
                    // Upgrade scope if we found it in entity attrs but didn't know it was in solution
                    existing.Scope = OptionSetScope.GlobalInSolution;
                    if (existing.InlineMetadata == null) existing.InlineMetadata = osm;
                }
                else
                {
                    seen[name] = new OptionSetSummary
                    {
                        Name = name,
                        DisplayName = osm.DisplayName?.UserLocalizedLabel?.Label ?? name,
                        MetadataId = osm.MetadataId ?? Guid.Empty,
                        Scope = OptionSetScope.GlobalInSolution,
                        InlineMetadata = osm
                    };
                }
            }

            result.Summaries = seen.Values
                .OrderBy(s => (int)s.Scope)
                .ThenBy(s => string.IsNullOrEmpty(s.DisplayName) ? s.Name : s.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            return result;
        }

        // Build detail from pre-fetched inline data — zero API calls
        public static OptionSetDetail GetDetailFromInline(
            OptionSetMetadata osm,
            OptionSetScope scope,
            Dictionary<string, List<OptionSetUsage>> usageMap)
        {
            var name = osm.Name ?? "";
            var detail = new OptionSetDetail
            {
                Name = name,
                DisplayName = osm.DisplayName?.UserLocalizedLabel?.Label ?? name,
                Description = osm.Description?.UserLocalizedLabel?.Label ?? "",
                Scope = scope == OptionSetScope.Local ? "local"
                      : scope == OptionSetScope.GlobalNotInSolution ? "globalNotInSolution"
                      : "global"
            };

            if (osm.Options != null)
                foreach (var opt in osm.Options.OrderBy(o => o.Value ?? 0))
                    detail.Values.Add(new OptionValue
                    {
                        Value = opt.Value ?? 0,
                        Label = opt.Label?.UserLocalizedLabel?.Label ?? "",
                        Color = opt.Color ?? "",
                        Description = opt.Description?.UserLocalizedLabel?.Label ?? ""
                    });

            if (usageMap.TryGetValue(name, out var uses))
            {
                detail.UsedBy.AddRange(uses);
                detail.UsedBy.Sort((a, b) =>
                {
                    int t = string.Compare(a.TableDisplay, b.TableDisplay, StringComparison.OrdinalIgnoreCase);
                    return t != 0 ? t : string.Compare(a.ColumnDisplay, b.ColumnDisplay, StringComparison.OrdinalIgnoreCase);
                });
            }

            return detail;
        }

        private static Dictionary<string, List<OptionSetUsage>> BuildUsageMap(List<EntityMetadata> entityMeta)
        {
            var map = new Dictionary<string, List<OptionSetUsage>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entityMeta)
            {
                var tableDisplay = entity.DisplayName?.UserLocalizedLabel?.Label ?? entity.LogicalName;
                foreach (var attr in entity.Attributes)
                {
                    if (!(attr is PicklistAttributeMetadata pl) || pl.OptionSet == null) continue;
                    var name = pl.OptionSet.Name ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!map.ContainsKey(name)) map[name] = new List<OptionSetUsage>();
                    map[name].Add(new OptionSetUsage
                    {
                        TableName = entity.LogicalName,
                        TableDisplay = tableDisplay,
                        ColumnName = attr.LogicalName,
                        ColumnDisplay = attr.DisplayName?.UserLocalizedLabel?.Label ?? attr.LogicalName
                    });
                }
            }
            return map;
        }

        // ── Legacy: single option set in solution list ─────────────────────────
        public List<OptionSetSummary> GetOptionSetsInSolution(Guid solutionId)
        {
            var compQ = new QueryExpression("solutioncomponent")
            {
                ColumnSet = new ColumnSet("objectid")
            };
            compQ.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            compQ.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 9);

            var ids = _service.RetrieveMultiple(compQ).Entities
                .Select(e => e.GetAttributeValue<Guid>("objectid"))
                .ToHashSet();

            if (ids.Count == 0) return new List<OptionSetSummary>();

            var resp = (RetrieveAllOptionSetsResponse)_service.Execute(
                new RetrieveAllOptionSetsRequest { RetrieveAsIfPublished = false });

            return resp.OptionSetMetadata
                .Where(os => ids.Contains(os.MetadataId ?? Guid.Empty))
                .OfType<OptionSetMetadata>()
                .Select(os => new OptionSetSummary
                {
                    Name = os.Name ?? "",
                    DisplayName = os.DisplayName?.UserLocalizedLabel?.Label ?? os.Name ?? "",
                    MetadataId = os.MetadataId ?? Guid.Empty
                })
                .OrderBy(s => s.DisplayName)
                .ToList();
        }

        // ── Legacy detail fetch (used only as fallback) ────────────────────────
        public OptionSetDetail GetDetail(string optionSetName, IEnumerable<string> solutionEntityNames)
        {
            var osResp = (RetrieveOptionSetResponse)_service.Execute(
                new RetrieveOptionSetRequest { Name = optionSetName, RetrieveAsIfPublished = false });

            var os = (OptionSetMetadata)osResp.OptionSetMetadata;

            var detail = new OptionSetDetail
            {
                Name = os.Name ?? "",
                DisplayName = os.DisplayName?.UserLocalizedLabel?.Label ?? os.Name ?? "",
                Description = os.Description?.UserLocalizedLabel?.Label ?? "",
                Scope = "global"
            };

            foreach (var opt in os.Options.OrderBy(o => o.Value ?? 0))
                detail.Values.Add(new OptionValue
                {
                    Value = opt.Value ?? 0,
                    Label = opt.Label?.UserLocalizedLabel?.Label ?? "",
                    Color = opt.Color ?? "",
                    Description = opt.Description?.UserLocalizedLabel?.Label ?? ""
                });

            var entityNames = solutionEntityNames.ToList();
            if (entityNames.Count > 0)
            {
                var batch = new ExecuteMultipleRequest
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
                };
                foreach (var name in entityNames)
                    batch.Requests.Add(new RetrieveEntityRequest
                    {
                        LogicalName = name,
                        EntityFilters = EntityFilters.Attributes
                    });

                var batchResp = (ExecuteMultipleResponse)_service.Execute(batch);
                foreach (var item in batchResp.Responses)
                {
                    if (item.Fault != null || item.Response == null) continue;
                    var meta = ((RetrieveEntityResponse)item.Response).EntityMetadata;
                    foreach (var attr in meta.Attributes)
                    {
                        if (!UsesGlobalOptionSet(attr, optionSetName)) continue;
                        detail.UsedBy.Add(new OptionSetUsage
                        {
                            TableName = meta.LogicalName,
                            TableDisplay = meta.DisplayName?.UserLocalizedLabel?.Label ?? meta.LogicalName,
                            ColumnName = attr.LogicalName,
                            ColumnDisplay = attr.DisplayName?.UserLocalizedLabel?.Label ?? attr.LogicalName
                        });
                    }
                }
            }

            detail.UsedBy.Sort((a, b) =>
            {
                int t = string.Compare(a.TableDisplay, b.TableDisplay, StringComparison.OrdinalIgnoreCase);
                return t != 0 ? t : string.Compare(a.ColumnDisplay, b.ColumnDisplay, StringComparison.OrdinalIgnoreCase);
            });

            return detail;
        }

        private static bool UsesGlobalOptionSet(AttributeMetadata attr, string optionSetName)
        {
            if (attr is PicklistAttributeMetadata pl)
                return pl.OptionSet?.IsGlobal == true &&
                       string.Equals(pl.OptionSet.Name, optionSetName, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // ── JSON ─────────────────────────────────────────────────────────────
        public static string ToJson(OptionSetDetail d)
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"name\":{Js(d.Name)},\"displayName\":{Js(d.DisplayName)},\"description\":{Js(d.Description)},\"scope\":{Js(d.Scope)},");
            sb.Append("\"values\":[");
            for (int i = 0; i < d.Values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var v = d.Values[i];
                sb.Append($"{{\"value\":{v.Value},\"label\":{Js(v.Label)},\"color\":{Js(v.Color)},\"description\":{Js(v.Description)}}}");
            }
            sb.Append("],\"usedBy\":[");
            for (int i = 0; i < d.UsedBy.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var u = d.UsedBy[i];
                sb.Append($"{{\"tableName\":{Js(u.TableName)},\"tableDisplay\":{Js(u.TableDisplay)},\"columnName\":{Js(u.ColumnName)},\"columnDisplay\":{Js(u.ColumnDisplay)}}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Js(string? s) => s == null ? "null"
            : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }

    internal class OptionSetSummary
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public Guid MetadataId { get; set; }
        public OptionSetScope Scope { get; set; } = OptionSetScope.GlobalInSolution;
        public OptionSetMetadata? InlineMetadata { get; set; }

        public override string ToString()
        {
            var display = string.IsNullOrEmpty(DisplayName) ? Name : $"{DisplayName} ({Name})";
            return Scope switch
            {
                OptionSetScope.Local => $"{display}  [local]",
                OptionSetScope.GlobalNotInSolution => $"{display}  [not in solution]",
                _ => display
            };
        }
    }

    internal class OptionSetDetail
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Scope { get; set; } = "global";
        public List<OptionValue> Values { get; } = new();
        public List<OptionSetUsage> UsedBy { get; } = new();
    }

    internal class OptionValue
    {
        public int Value { get; set; }
        public string Label { get; set; } = "";
        public string Color { get; set; } = "";
        public string Description { get; set; } = "";
    }

    internal class OptionSetUsage
    {
        public string TableName { get; set; } = "";
        public string TableDisplay { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string ColumnDisplay { get; set; } = "";
    }
}
