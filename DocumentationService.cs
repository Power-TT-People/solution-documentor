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
    internal class DocOptions
    {
        public bool IncludeTables { get; set; } = true;
        public bool IncludeOptionSets { get; set; } = true;
        public bool IncludeRoles { get; set; } = true;
        public bool IncludeFlows { get; set; } = true;
    }

    internal class DocumentationService
    {
        private readonly IOrganizationService _service;

        public DocumentationService(IOrganizationService service) => _service = service;

        public string Generate(Guid solutionId, string solutionName,
            DocOptions opts, Action<string> progress)
        {
            // Call 1: all component IDs in one query
            progress("Loading solution components…");
            var (entityIds, optionSetIds, roleIds, flowIds) = FetchAllComponentIds(solutionId);

            // Call 2: entity metadata batch
            List<EntityMetadata>? entityMeta = null;
            if (opts.IncludeTables || opts.IncludeOptionSets)
            {
                if (entityIds.Count > 0)
                {
                    progress($"Fetching metadata for {entityIds.Count} entities…");
                    entityMeta = FetchEntityMetadataByIds(entityIds);
                }
                else
                {
                    entityMeta = new List<EntityMetadata>();
                }
            }

            // Call 3: fetch option sets early so table columns can link into them
            var osMetaList = new List<OptionSetMetadata>();
            Dictionary<string, string>? osAnchors = null;
            if (opts.IncludeOptionSets && optionSetIds.Count > 0)
            {
                progress("Loading option sets…");
                var allOsResp = (RetrieveAllOptionSetsResponse)_service.Execute(
                    new RetrieveAllOptionSetsRequest { RetrieveAsIfPublished = false });
                var idSet = new HashSet<Guid>(optionSetIds);
                osMetaList = allOsResp.OptionSetMetadata
                    .Where(os => idSet.Contains(os.MetadataId ?? Guid.Empty))
                    .OfType<OptionSetMetadata>()
                    .OrderBy(os => os.DisplayName?.UserLocalizedLabel?.Label ?? os.Name ?? "")
                    .ToList();
                if (opts.IncludeTables)
                    osAnchors = osMetaList.ToDictionary(
                        os => os.Name ?? "",
                        os => MdAnchor(os.DisplayName?.UserLocalizedLabel?.Label ?? os.Name ?? "", os.Name ?? ""),
                        StringComparer.OrdinalIgnoreCase);
            }

            // Pre-fetch role and flow summaries (used for TOC and section writing)
            var roleSvc = new SecurityRoleService(_service);
            var flowSvc = new FlowDocService(_service);

            var roles = new List<RoleSummary>();
            var flows = new List<FlowSummary>();

            if (opts.IncludeRoles && roleIds.Count > 0)
            {
                progress("Fetching security roles…");
                roles = roleSvc.GetRolesByIds(roleIds);
            }

            if (opts.IncludeFlows && flowIds.Count > 0)
            {
                progress("Fetching flows…");
                flows = flowSvc.GetFlowsByIds(flowIds);
            }

            // ── Write document ────────────────────────────────────────────────
            var sb = new StringBuilder();
            sb.Line("*Powered by your friends at Pragmatic Works*");
            sb.Line();
            sb.Line($"# {solutionName} — Solution Documentation");
            sb.Line();
            sb.Line($"*Generated {DateTime.Now:dd MMM yyyy, HH:mm}*");
            sb.Line();
            sb.Line("---");
            sb.Line();

            WriteToc(sb, entityMeta, osMetaList, roles, flows, opts);

            if (opts.IncludeTables && entityMeta != null)
            {
                progress("Writing Tables section…");
                WriteTables(sb, entityMeta, osAnchors);
            }

            if (opts.IncludeOptionSets && osMetaList.Count > 0)
            {
                progress("Writing Option Sets section…");
                WriteOptionSets(sb, osMetaList, entityMeta);
            }

            if (opts.IncludeRoles && roles.Count > 0)
            {
                progress("Writing security roles…");
                WriteRoles(sb, roles, roleSvc, ToEntityInfos(entityMeta));
            }

            if (opts.IncludeFlows && flows.Count > 0)
            {
                progress("Writing flows…");
                WriteFlows(sb, flows, flowSvc);
            }

            return sb.ToString();
        }

        // ── Call 1: all solution component IDs in one query ───────────────────
        private (List<Guid> entities, List<Guid> optionSets, List<Guid> roles, List<Guid> flows)
            FetchAllComponentIds(Guid solutionId)
        {
            var compQ = new QueryExpression("solutioncomponent")
            {
                ColumnSet = new ColumnSet("objectid", "componenttype")
            };
            compQ.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            compQ.Criteria.AddCondition("componenttype", ConditionOperator.In,
                new object[] { 1, 9, 20, 29 });

            var entities = new List<Guid>();
            var optionSets = new List<Guid>();
            var roles = new List<Guid>();
            var flows = new List<Guid>();

            foreach (var e in _service.RetrieveMultiple(compQ).Entities)
            {
                var id = e.GetAttributeValue<Guid>("objectid");
                var type = e.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
                switch (type)
                {
                    case 1: entities.Add(id); break;
                    case 9: optionSets.Add(id); break;
                    case 20: roles.Add(id); break;
                    case 29: flows.Add(id); break;
                }
            }

            return (entities, optionSets, roles, flows);
        }

        // ── Call 2: entity metadata by MetadataId ─────────────────────────────
        private List<EntityMetadata> FetchEntityMetadataByIds(List<Guid> ids)
        {
            const int chunkSize = 50;
            var result = new List<EntityMetadata>();

            for (int i = 0; i < ids.Count; i += chunkSize)
            {
                var chunk = ids.Skip(i).Take(chunkSize).ToList();
                var batch = new ExecuteMultipleRequest
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
                };
                foreach (var id in chunk)
                    batch.Requests.Add(new RetrieveEntityRequest
                    {
                        MetadataId = id,
                        EntityFilters = EntityFilters.Attributes | EntityFilters.Relationships
                    });

                var resp = (ExecuteMultipleResponse)_service.Execute(batch);
                result.AddRange(resp.Responses
                    .Where(r => r.Fault == null && r.Response != null)
                    .Select(r => ((RetrieveEntityResponse)r.Response).EntityMetadata));
            }

            return result
                .OrderBy(m => m.DisplayName?.UserLocalizedLabel?.Label ?? m.LogicalName)
                .ToList();
        }

        // ── Table of Contents ─────────────────────────────────────────────────
        private static void WriteToc(StringBuilder sb,
            List<EntityMetadata>? entityMeta,
            List<OptionSetMetadata> osMetaList,
            List<RoleSummary> roles,
            List<FlowSummary> flows,
            DocOptions opts)
        {
            bool any =
                (opts.IncludeTables && entityMeta?.Count > 0) ||
                (opts.IncludeOptionSets && osMetaList.Count > 0) ||
                (opts.IncludeRoles && roles.Count > 0) ||
                (opts.IncludeFlows && flows.Count > 0);

            if (!any) return;

            sb.Line("## Contents");
            sb.Line();

            if (opts.IncludeTables && entityMeta?.Count > 0)
            {
                var n = entityMeta.Count;
                sb.Line($"- [Tables ({n})](#{HeadingAnchor($"Tables ({n})")})");
                foreach (var e in entityMeta)
                {
                    var display = e.DisplayName?.UserLocalizedLabel?.Label ?? e.LogicalName;
                    sb.Line($"  - [{Md(display)} (`{e.LogicalName}`)](#{MdAnchor(display, e.LogicalName)})");
                }
                sb.Line();
            }

            if (opts.IncludeOptionSets && osMetaList.Count > 0)
            {
                var n = osMetaList.Count;
                sb.Line($"- [Global Option Sets ({n})](#{HeadingAnchor($"Global Option Sets ({n})")})");
                foreach (var osm in osMetaList)
                {
                    var displayName = osm.DisplayName?.UserLocalizedLabel?.Label ?? osm.Name ?? "";
                    sb.Line($"  - [{Md(displayName)} (`{osm.Name}`)](#{MdAnchor(displayName, osm.Name ?? "")})");
                }
                sb.Line();
            }

            if (opts.IncludeRoles && roles.Count > 0)
            {
                var n = roles.Count;
                sb.Line($"- [Security Roles ({n})](#{HeadingAnchor($"Security Roles ({n})")})");
                foreach (var r in roles)
                    sb.Line($"  - [{Md(r.Name)}](#{HeadingAnchor(r.Name)})");
                sb.Line();
            }

            if (opts.IncludeFlows && flows.Count > 0)
            {
                var n = flows.Count;
                sb.Line($"- [Flows ({n})](#{HeadingAnchor($"Flows ({n})")})");
                foreach (var f in flows)
                {
                    var status = f.IsActive ? "● Active" : "○ Draft";
                    sb.Line($"  - [{Md(f.Name)} — {status}](#{HeadingAnchor($"{f.Name} — {status}")})");
                }
                sb.Line();
            }

            sb.Line("---");
            sb.Line();
        }

        // ── Tables ────────────────────────────────────────────────────────────
        private static void WriteTables(StringBuilder sb, List<EntityMetadata> entities,
            Dictionary<string, string>? osAnchors)
        {
            sb.Line($"## Tables ({entities.Count})");
            sb.Line();

            // Entity link map for lookup column cross-links — built once for all entities
            var entityLinks = entities.ToDictionary(
                en => en.LogicalName,
                en => (
                    anchor: MdAnchor(en.DisplayName?.UserLocalizedLabel?.Label ?? en.LogicalName, en.LogicalName),
                    display: en.DisplayName?.UserLocalizedLabel?.Label ?? en.LogicalName
                ),
                StringComparer.OrdinalIgnoreCase);

            foreach (var e in entities)
            {
                var display = e.DisplayName?.UserLocalizedLabel?.Label ?? e.LogicalName;
                var desc = e.Description?.UserLocalizedLabel?.Label ?? "";

                sb.Line($"### {display} (`{e.LogicalName}`)");
                if (!string.IsNullOrWhiteSpace(desc)) sb.Line($"> {desc}");
                sb.Line();

                var attrs = e.Attributes
                    .Where(a => a.AttributeOf == null && a.AttributeType != AttributeTypeCode.Virtual)
                    .OrderBy(a => a.IsPrimaryId == true ? 0 : 1)
                    .ThenBy(a => a.LogicalName)
                    .ToList();

                // Pre-compute which choice columns will have an inline value table below,
                // so we can link to them from the Type column before we write the table
                var unlinkedChoices = attrs
                    .OfType<PicklistAttributeMetadata>()
                    .Where(pl => pl.OptionSet?.Options != null
                        && pl.OptionSet.Options.Count > 0
                        && !(pl.OptionSet.IsGlobal == true
                             && osAnchors != null
                             && osAnchors.ContainsKey(pl.OptionSet.Name ?? "")))
                    .ToList();

                var localAnchors = unlinkedChoices.ToDictionary(
                    pl => pl.LogicalName,
                    pl => MdAnchor(pl.DisplayName?.UserLocalizedLabel?.Label ?? pl.LogicalName, pl.LogicalName),
                    StringComparer.OrdinalIgnoreCase);

                sb.Line("| Column | Display Name | Type | Required | Description | Constraints |");
                sb.Line("|--------|-------------|------|----------|-------------|-------------|");
                foreach (var a in attrs)
                {
                    var aDisplay = a.DisplayName?.UserLocalizedLabel?.Label ?? a.LogicalName;
                    var aDesc = a.Description?.UserLocalizedLabel?.Label ?? "";
                    var type = AttrTypeWithLink(a, osAnchors, localAnchors, entityLinks);
                    var constraints = AttrConstraints(a);
                    var req = a.RequiredLevel?.Value == AttributeRequiredLevel.ApplicationRequired
                           || a.RequiredLevel?.Value == AttributeRequiredLevel.SystemRequired
                        ? "✓" : "";
                    sb.Line($"| `{a.LogicalName}` | {Md(aDisplay)} | {type} | {req} | {Md(aDesc)} | {Md(constraints)} |");
                }
                sb.Line();

                // Individual choice value tables (local OS and global-not-in-solution)
                // Data already in entityMeta — zero extra API calls
                if (unlinkedChoices.Count > 0)
                {
                    sb.Line("**Choice values:**");
                    sb.Line();
                    foreach (var pl in unlinkedChoices)
                    {
                        var aDisplay = pl.DisplayName?.UserLocalizedLabel?.Label ?? pl.LogicalName;
                        sb.Line($"#### {Md(aDisplay)} (`{pl.LogicalName}`)");
                        sb.Line();
                        sb.Line("| Value | Label | Color |");
                        sb.Line("|-------|-------|-------|");
                        foreach (var opt in pl.OptionSet.Options.OrderBy(o => o.Value ?? 0))
                        {
                            var label = opt.Label?.UserLocalizedLabel?.Label ?? "";
                            var color = opt.Color ?? "";
                            sb.Line($"| {opt.Value} | {Md(label)} | {(string.IsNullOrEmpty(color) ? "" : $"`{color}`")} |");
                        }
                        sb.Line();
                    }
                }

                var solutionEntityNames = entities.Select(x => x.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var otmRels = e.OneToManyRelationships
                    .Where(r => solutionEntityNames.Contains(r.ReferencingEntity) && r.ReferencingEntity != e.LogicalName)
                    .ToList();
                var mtoRels = e.ManyToOneRelationships
                    .Where(r => solutionEntityNames.Contains(r.ReferencedEntity) && r.ReferencedEntity != e.LogicalName)
                    .ToList();
                var mtmRels = e.ManyToManyRelationships
                    .Where(r => solutionEntityNames.Contains(r.Entity1LogicalName == e.LogicalName
                        ? r.Entity2LogicalName : r.Entity1LogicalName))
                    .ToList();

                if (otmRels.Any() || mtoRels.Any() || mtmRels.Any())
                {
                    sb.Line("**Relationships to other solution entities:**");
                    foreach (var r in mtoRels)
                        sb.Line($"- N:1 → `{r.ReferencedEntity}` via `{r.ReferencingAttribute}` *(lookup)*");
                    foreach (var r in otmRels)
                        sb.Line($"- 1:N → `{r.ReferencingEntity}` via `{r.ReferencingAttribute}`");
                    foreach (var r in mtmRels)
                    {
                        var other = r.Entity1LogicalName == e.LogicalName ? r.Entity2LogicalName : r.Entity1LogicalName;
                        sb.Line($"- N:N ↔ `{other}` *(intersect: {r.IntersectEntityName})*");
                    }
                    sb.Line();
                }

                sb.Line("---");
                sb.Line();
            }
        }

        // ── Option Sets (uses pre-fetched metadata — no extra API call) ───────
        private static void WriteOptionSets(StringBuilder sb, List<OptionSetMetadata> list,
            List<EntityMetadata>? entityMeta)
        {
            if (list.Count == 0) return;

            var usageMap = BuildOptionSetUsageMap(entityMeta);

            sb.Line($"## Global Option Sets ({list.Count})");
            sb.Line();

            foreach (var osm in list)
            {
                var displayName = osm.DisplayName?.UserLocalizedLabel?.Label ?? osm.Name ?? "";
                var description = osm.Description?.UserLocalizedLabel?.Label ?? "";

                sb.Line($"### {Md(displayName)} (`{osm.Name}`)");
                if (!string.IsNullOrWhiteSpace(description)) sb.Line($"> {Md(description)}");
                sb.Line();

                sb.Line("| Value | Label | Color |");
                sb.Line("|-------|-------|-------|");
                foreach (var opt in osm.Options.OrderBy(o => o.Value ?? 0))
                {
                    var label = opt.Label?.UserLocalizedLabel?.Label ?? "";
                    var color = opt.Color ?? "";
                    sb.Line($"| {opt.Value} | {Md(label)} | {(string.IsNullOrEmpty(color) ? "" : $"`{color}`")} |");
                }
                sb.Line();

                if (usageMap.TryGetValue(osm.Name ?? "", out var uses) && uses.Count > 0)
                {
                    var usage = uses
                        .GroupBy(u => u.tableDisplay)
                        .Select(g => $"{g.Key} ({string.Join(", ", g.Select(u => u.colDisplay))})");
                    sb.Line($"*Used by: {string.Join(" · ", usage)}*");
                    sb.Line();
                }

                sb.Line("---");
                sb.Line();
            }
        }

        private static Dictionary<string, List<(string tableDisplay, string colDisplay)>>
            BuildOptionSetUsageMap(List<EntityMetadata>? entityMeta)
        {
            var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            if (entityMeta == null) return map;

            foreach (var entity in entityMeta)
            {
                var tableDisplay = entity.DisplayName?.UserLocalizedLabel?.Label ?? entity.LogicalName;
                foreach (var attr in entity.Attributes)
                {
                    string? osName = null;
                    if (attr is PicklistAttributeMetadata pl && pl.OptionSet?.IsGlobal == true)
                        osName = pl.OptionSet.Name;
                    if (osName == null) continue;
                    var colDisplay = attr.DisplayName?.UserLocalizedLabel?.Label ?? attr.LogicalName;
                    if (!map.ContainsKey(osName)) map[osName] = new List<(string, string)>();
                    map[osName].Add((tableDisplay, colDisplay));
                }
            }
            return map;
        }

        // ── Security Roles ────────────────────────────────────────────────────
        private static void WriteRoles(StringBuilder sb, List<RoleSummary> roles,
            SecurityRoleService roleSvc, List<EntityInfo> entityInfos)
        {
            if (roles.Count == 0) return;

            var detailMap = roleSvc.GetAllDetails(roles);
            var permMap = entityInfos.Count > 0
                ? roleSvc.GetAllEntityPermissions(roles.Select(r => r.Id), entityInfos)
                : new Dictionary<Guid, List<EntityPermission>>();

            sb.Line($"## Security Roles ({roles.Count})");
            sb.Line();

            foreach (var role in roles)
            {
                if (!detailMap.TryGetValue(role.Id, out var detail)) continue;

                sb.Line($"### {Md(detail.Name)}");
                if (!string.IsNullOrEmpty(detail.BusinessUnit))
                    sb.Line($"**Business Unit:** {Md(detail.BusinessUnit)}");
                sb.Line();

                // Entity permissions table
                if (permMap.TryGetValue(role.Id, out var perms) && perms.Count > 0)
                {
                    sb.Line("#### Entity Permissions");
                    sb.Line();
                    sb.Line("| Entity | Create | Read | Write | Delete | Append | Append To | Assign | Share |");
                    sb.Line("|--------|--------|------|-------|--------|--------|-----------|--------|-------|");
                    foreach (var p in perms)
                        sb.Line($"| {Md(p.EntityDisplayName)} | {LevelLabel(p.Create)} | {LevelLabel(p.Read)} | {LevelLabel(p.Write)} | {LevelLabel(p.Delete)} | {LevelLabel(p.Append)} | {LevelLabel(p.AppendTo)} | {LevelLabel(p.Assign)} | {LevelLabel(p.Share)} |");
                    sb.Line();
                }

                sb.Line($"**Users ({detail.Users.Count}):**");
                if (detail.Users.Count == 0)
                    sb.Line("*No active users are directly assigned this role.*");
                else
                {
                    sb.Line("| Name | Email | Business Unit |");
                    sb.Line("|------|-------|---------------|");
                    foreach (var u in detail.Users)
                        sb.Line($"| {Md(u.Name)} | {Md(u.Email)} | {Md(u.BusinessUnit)} |");
                }
                sb.Line();

                sb.Line($"**Teams ({detail.Teams.Count}):**");
                if (detail.Teams.Count == 0)
                    sb.Line("*No teams are assigned this role.*");
                else
                {
                    sb.Line("| Name | Type | Business Unit |");
                    sb.Line("|------|------|---------------|");
                    foreach (var t in detail.Teams)
                        sb.Line($"| {Md(t.Name)} | {Md(t.TeamType)} | {Md(t.BusinessUnit)} |");
                }
                sb.Line();

                sb.Line("---");
                sb.Line();
            }
        }

        // ── Flows ─────────────────────────────────────────────────────────────
        private static void WriteFlows(StringBuilder sb, List<FlowSummary> flows, FlowDocService flowSvc)
        {
            if (flows.Count == 0) return;

            var detailMap = flowSvc.GetAllDetails(flows);

            sb.Line($"## Flows ({flows.Count})");
            sb.Line();

            foreach (var flow in flows)
            {
                if (!detailMap.TryGetValue(flow.Id, out var detail)) continue;

                var status = detail.IsActive ? "● Active" : "○ Draft";
                sb.Line($"### {Md(detail.Name)} — {status}");

                if (!string.IsNullOrWhiteSpace(detail.Description))
                    sb.Line($"> {Md(detail.Description)}");
                sb.Line();

                if (detail.Trigger != null)
                    sb.Line($"**Trigger:** {Md(detail.Trigger.Description)}");
                sb.Line();

                var actionSteps = detail.Steps.Where(s => !s.IsBranchLabel).ToList();
                sb.Line($"**Steps ({actionSteps.Count}):**");
                sb.Line();

                WriteFlowSteps(sb, detail.Steps, 0);
                sb.Line();

                sb.Line("---");
                sb.Line();
            }
        }

        private static void WriteFlowSteps(StringBuilder sb, List<FlowStep> steps, int baseIndent)
        {
            string Indent(int n) => new string(' ', n * 2);

            foreach (var s in steps)
            {
                var ind = Indent(s.Indent - baseIndent);

                if (s.IsBranchLabel)
                {
                    sb.Line($"{ind}**▸ {Md(s.Name)}**");
                    continue;
                }

                var badge = string.IsNullOrEmpty(s.Badge) ? "" : $" `{s.Badge}`";
                sb.Line($"{ind}{s.StepNumber}. **{Md(s.Name)}**{badge}");

                if (!string.IsNullOrWhiteSpace(s.Description))
                    sb.Line($"{ind}   {Md(s.Description)}");

                foreach (var p in s.Parameters)
                    sb.Line($"{ind}   - **{Md(p.Key)}:** {Md(p.Value)}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Returns the Type cell value with appropriate markdown link:
        //   global OS in solution      → [Choice](#os-section-anchor)
        //   local OS / global-not-doc  → [Choice](#local-table-anchor-below)
        //   lookup to entity in doc    → [Lookup → Entity](#entity-anchor)  (or multi-link)
        //   anything else              → plain type label
        private static string AttrTypeWithLink(
            AttributeMetadata a,
            Dictionary<string, string>? osAnchors,
            Dictionary<string, string>? localAnchors,
            Dictionary<string, (string anchor, string display)>? entityLinks)
        {
            // ── Choice columns ───────────────────────────────────────────────
            if (a is PicklistAttributeMetadata pl && pl.OptionSet != null)
            {
                if (pl.OptionSet.IsGlobal == true)
                {
                    var osName = pl.OptionSet.Name ?? "";
                    if (osAnchors != null && osAnchors.TryGetValue(osName, out var osAnchor))
                        return $"[Choice](#{osAnchor})";
                }
                // Local OS or global-not-in-solution: link to inline table below entity section
                if (localAnchors != null && localAnchors.TryGetValue(a.LogicalName, out var localAnchor))
                    return $"[Choice](#{localAnchor})";

                var fallbackName = pl.OptionSet.Name ?? "";
                return string.IsNullOrEmpty(fallbackName) ? "Choice" : $"Choice (`{fallbackName}`)";
            }

            // ── Lookup columns ───────────────────────────────────────────────
            if (entityLinks != null && a is LookupAttributeMetadata la && la.Targets != null)
            {
                var linked = la.Targets.Where(t => entityLinks.ContainsKey(t)).ToList();
                if (linked.Count > 0)
                {
                    var typeLabel = AttrType(a); // Lookup, Customer, or Owner
                    if (linked.Count == 1)
                    {
                        var (anchor, disp) = entityLinks[linked[0]];
                        return $"[{typeLabel} → {Md(disp)}](#{anchor})";
                    }
                    var parts = string.Join(" / ", linked.Select(t =>
                    {
                        var (anchor, disp) = entityLinks[t];
                        return $"[{Md(disp)}](#{anchor})";
                    }));
                    return $"{typeLabel} → {parts}";
                }
            }

            return AttrType(a);
        }

        private static string AttrType(AttributeMetadata a)
        {
            return a.AttributeType switch
            {
                AttributeTypeCode.String => "Text",
                AttributeTypeCode.Memo => "Memo",
                AttributeTypeCode.Integer => "Integer",
                AttributeTypeCode.BigInt => "Big Integer",
                AttributeTypeCode.Decimal => "Decimal",
                AttributeTypeCode.Double => "Float",
                AttributeTypeCode.Money => "Currency",
                AttributeTypeCode.Boolean => "Yes/No",
                AttributeTypeCode.DateTime => "Date/Time",
                AttributeTypeCode.Lookup => "Lookup",
                AttributeTypeCode.Customer => "Customer",
                AttributeTypeCode.Owner => "Owner",
                AttributeTypeCode.Picklist => "Choice",
                AttributeTypeCode.State => "State",
                AttributeTypeCode.Status => "Status",
                AttributeTypeCode.Uniqueidentifier => "Unique Identifier",
                AttributeTypeCode.EntityName => "Entity Name",
                _ => a.AttributeType?.ToString() ?? "Unknown"
            };
        }

        private static string AttrConstraints(AttributeMetadata a)
        {
            switch (a)
            {
                case StringAttributeMetadata s:
                    return s.MaxLength.HasValue ? $"Max {s.MaxLength} chars" : "";

                case MemoAttributeMetadata m:
                    return m.MaxLength.HasValue ? $"Max {m.MaxLength} chars" : "";

                case IntegerAttributeMetadata i:
                {
                    var parts = new List<string>();
                    // Only show if the range is actually constrained (not the full int range)
                    if (i.MinValue.HasValue && i.MinValue.Value != int.MinValue)
                        parts.Add($"Min {i.MinValue}");
                    if (i.MaxValue.HasValue && i.MaxValue.Value != int.MaxValue)
                        parts.Add($"Max {i.MaxValue}");
                    return string.Join(", ", parts);
                }

                case DecimalAttributeMetadata d:
                {
                    var parts = new List<string>();
                    if (d.MinValue.HasValue) parts.Add($"Min {d.MinValue}");
                    if (d.MaxValue.HasValue) parts.Add($"Max {d.MaxValue}");
                    if (d.Precision.HasValue) parts.Add($"Precision {d.Precision}");
                    return string.Join(", ", parts);
                }

                case DoubleAttributeMetadata db:
                {
                    var parts = new List<string>();
                    if (db.MinValue.HasValue) parts.Add($"Min {db.MinValue}");
                    if (db.MaxValue.HasValue) parts.Add($"Max {db.MaxValue}");
                    if (db.Precision.HasValue) parts.Add($"Precision {db.Precision}");
                    return string.Join(", ", parts);
                }

                case MoneyAttributeMetadata mo:
                {
                    var parts = new List<string>();
                    if (mo.MinValue.HasValue) parts.Add($"Min {mo.MinValue}");
                    if (mo.MaxValue.HasValue) parts.Add($"Max {mo.MaxValue}");
                    if (mo.Precision.HasValue) parts.Add($"Precision {mo.Precision}");
                    return string.Join(", ", parts);
                }

                default:
                    return "";
            }
        }

        // GitHub-style heading anchor from raw text: lowercase, keep [a-z0-9_],
        // space/hyphen → hyphen, everything else dropped, consecutive hyphens collapsed.
        private static string HeadingAnchor(string text)
        {
            var sb = new StringBuilder();
            bool lastHyphen = false;
            foreach (var c in text.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                {
                    sb.Append(c);
                    lastHyphen = false;
                }
                else if (c == ' ' || c == '-')
                {
                    if (!lastHyphen) sb.Append('-');
                    lastHyphen = true;
                }
                // else: drop the character
            }
            return sb.ToString();
        }

        // Computes the GitHub-style heading anchor for: ### {displayName} (`{logicalName}`)
        private static string MdAnchor(string displayName, string logicalName)
            => HeadingAnchor($"{displayName} (`{logicalName}`)");

        // Maps internal level strings to readable labels for the permissions table.
        private static string LevelLabel(string level) => level switch
        {
            "user" => "User",
            "bu"   => "BU",
            "deep" => "Deep",
            "org"  => "Org",
            _      => "·"
        };

        // Converts entity metadata list to the EntityInfo shape expected by SecurityRoleService.
        private static List<EntityInfo> ToEntityInfos(List<EntityMetadata>? entityMeta) =>
            entityMeta?.Select(m => new EntityInfo
            {
                LogicalName = m.LogicalName,
                DisplayName = m.DisplayName?.UserLocalizedLabel?.Label ?? m.LogicalName,
                MetadataId = m.MetadataId ?? Guid.Empty
            }).ToList() ?? new List<EntityInfo>();

        private static string Md(string? s)
        {
            if (s == null) return "";
            return s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "").Trim();
        }
    }

    internal static class StringBuilderExtensions
    {
        public static void Line(this StringBuilder sb, string text = "")
            => sb.AppendLine(text);
    }
}
