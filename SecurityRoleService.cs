using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace XrmDataversePlugin
{
    internal class SecurityRoleService
    {
        private readonly IOrganizationService _service;

        public SecurityRoleService(IOrganizationService service) => _service = service;

        // ── Roles by IDs (no solutioncomponent query — caller already has IDs) ─
        public List<RoleSummary> GetRolesByIds(List<Guid> roleIds)
        {
            if (roleIds.Count == 0) return new List<RoleSummary>();

            var roleQ = new QueryExpression("role")
            {
                ColumnSet = new ColumnSet("roleid", "name", "businessunitid")
            };
            roleQ.Criteria.AddCondition("roleid", ConditionOperator.In, roleIds.Cast<object>().ToArray());

            var buLink = roleQ.AddLink("businessunit", "businessunitid", "businessunitid");
            buLink.Columns = new ColumnSet("name");
            buLink.EntityAlias = "bu";
            roleQ.AddOrder("name", OrderType.Ascending);

            return _service.RetrieveMultiple(roleQ).Entities
                .Select(e => new RoleSummary
                {
                    Id = e.GetAttributeValue<Guid>("roleid"),
                    Name = e.GetAttributeValue<string>("name") ?? "",
                    BusinessUnit = e.GetAttributeValue<AliasedValue>("bu.name")?.Value as string ?? ""
                })
                .ToList();
        }

        // ── Roles in solution ────────────────────────────────────────────────
        public List<RoleSummary> GetRolesInSolution(Guid solutionId)
        {
            var compQ = new QueryExpression("solutioncomponent")
            {
                ColumnSet = new ColumnSet("objectid")
            };
            compQ.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            compQ.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 20);

            var roleIds = _service.RetrieveMultiple(compQ).Entities
                .Select(e => e.GetAttributeValue<Guid>("objectid"))
                .ToList();

            if (roleIds.Count == 0) return new List<RoleSummary>();

            var roleQ = new QueryExpression("role")
            {
                ColumnSet = new ColumnSet("roleid", "name", "businessunitid")
            };
            roleQ.Criteria.AddCondition("roleid", ConditionOperator.In,
                roleIds.Cast<object>().ToArray());

            var buLink = roleQ.AddLink("businessunit", "businessunitid", "businessunitid");
            buLink.Columns = new ColumnSet("name");
            buLink.EntityAlias = "bu";
            roleQ.AddOrder("name", OrderType.Ascending);

            return _service.RetrieveMultiple(roleQ).Entities
                .Select(e => new RoleSummary
                {
                    Id = e.GetAttributeValue<Guid>("roleid"),
                    Name = e.GetAttributeValue<string>("name") ?? "",
                    BusinessUnit = e.GetAttributeValue<AliasedValue>("bu.name")?.Value as string ?? ""
                })
                .ToList();
        }

        // ── Bulk detail fetch (2 queries for all roles) ──────────────────────
        public Dictionary<Guid, RoleDetail> GetAllDetails(List<RoleSummary> roles)
        {
            var map = roles.ToDictionary(
                r => r.Id,
                r => new RoleDetail { Name = r.Name, BusinessUnit = r.BusinessUnit });

            var roleIds = roles.Select(r => (object)r.Id).ToArray();

            var userQ = new QueryExpression("systemuser")
            {
                ColumnSet = new ColumnSet("fullname", "internalemailaddress")
            };
            userQ.Criteria.AddCondition("isdisabled", ConditionOperator.Equal, false);

            var surLink = userQ.AddLink("systemuserroles", "systemuserid", "systemuserid");
            surLink.Columns = new ColumnSet("roleid");
            surLink.EntityAlias = "sur";
            surLink.LinkCriteria.AddCondition("roleid", ConditionOperator.In, roleIds);

            var userBuLink = userQ.AddLink("businessunit", "businessunitid", "businessunitid");
            userBuLink.Columns = new ColumnSet("name");
            userBuLink.EntityAlias = "bu";
            userQ.AddOrder("fullname", OrderType.Ascending);

            foreach (var u in _service.RetrieveMultiple(userQ).Entities)
            {
                var av = u.GetAttributeValue<AliasedValue>("sur.roleid");
                var roleId = av?.Value is Guid g ? g : av?.Value is EntityReference er ? er.Id : Guid.Empty;
                if (roleId == Guid.Empty || !map.TryGetValue(roleId, out var d)) continue;
                d.Users.Add(new RoleUser
                {
                    Name = u.GetAttributeValue<string>("fullname") ?? "",
                    Email = u.GetAttributeValue<string>("internalemailaddress") ?? "",
                    BusinessUnit = u.GetAttributeValue<AliasedValue>("bu.name")?.Value as string ?? ""
                });
            }

            var teamQ = new QueryExpression("team")
            {
                ColumnSet = new ColumnSet("name", "businessunitid", "teamtype")
            };

            var trLink = teamQ.AddLink("teamroles", "teamid", "teamid");
            trLink.Columns = new ColumnSet("roleid");
            trLink.EntityAlias = "tr";
            trLink.LinkCriteria.AddCondition("roleid", ConditionOperator.In, roleIds);

            var teamBuLink = teamQ.AddLink("businessunit", "businessunitid", "businessunitid");
            teamBuLink.Columns = new ColumnSet("name");
            teamBuLink.EntityAlias = "bu";
            teamQ.AddOrder("name", OrderType.Ascending);

            foreach (var t in _service.RetrieveMultiple(teamQ).Entities)
            {
                var av = t.GetAttributeValue<AliasedValue>("tr.roleid");
                var roleId = av?.Value is Guid g ? g : av?.Value is EntityReference er ? er.Id : Guid.Empty;
                if (roleId == Guid.Empty || !map.TryGetValue(roleId, out var d)) continue;
                d.Teams.Add(new RoleTeam
                {
                    Name = t.GetAttributeValue<string>("name") ?? "",
                    BusinessUnit = t.GetAttributeValue<AliasedValue>("bu.name")?.Value as string ?? "",
                    TeamType = TeamTypeName(t.GetAttributeValue<OptionSetValue>("teamtype")?.Value ?? 0)
                });
            }

            return map;
        }

        // ── Single role detail with entity permissions ────────────────────────
        public RoleDetail GetDetail(Guid roleId, string roleName, string businessUnit,
            IEnumerable<EntityInfo>? solutionEntities = null)
        {
            var detail = new RoleDetail { Name = roleName, BusinessUnit = businessUnit };

            // Users
            var userQ = new QueryExpression("systemuser")
            {
                ColumnSet = new ColumnSet("fullname", "internalemailaddress", "businessunitid")
            };
            userQ.Criteria.AddCondition("isdisabled", ConditionOperator.Equal, false);
            var userRoleLink = userQ.AddLink("systemuserroles", "systemuserid", "systemuserid");
            userRoleLink.LinkCriteria.AddCondition("roleid", ConditionOperator.Equal, roleId);
            var userBuLink = userQ.AddLink("businessunit", "businessunitid", "businessunitid");
            userBuLink.Columns = new ColumnSet("name");
            userBuLink.EntityAlias = "bu";
            userQ.AddOrder("fullname", OrderType.Ascending);

            foreach (var u in _service.RetrieveMultiple(userQ).Entities)
                detail.Users.Add(new RoleUser
                {
                    Name = u.GetAttributeValue<string>("fullname") ?? "",
                    Email = u.GetAttributeValue<string>("internalemailaddress") ?? "",
                    BusinessUnit = u.GetAttributeValue<AliasedValue>("bu.name")?.Value as string ?? ""
                });

            // Teams
            var teamQ = new QueryExpression("team")
            {
                ColumnSet = new ColumnSet("name", "businessunitid", "teamtype")
            };
            var teamRoleLink = teamQ.AddLink("teamroles", "teamid", "teamid");
            teamRoleLink.LinkCriteria.AddCondition("roleid", ConditionOperator.Equal, roleId);
            var teamBuLink = teamQ.AddLink("businessunit", "businessunitid", "businessunitid");
            teamBuLink.Columns = new ColumnSet("name");
            teamBuLink.EntityAlias = "bu";
            teamQ.AddOrder("name", OrderType.Ascending);

            foreach (var t in _service.RetrieveMultiple(teamQ).Entities)
                detail.Teams.Add(new RoleTeam
                {
                    Name = t.GetAttributeValue<string>("name") ?? "",
                    BusinessUnit = t.GetAttributeValue<AliasedValue>("bu.name")?.Value as string ?? "",
                    TeamType = TeamTypeName(t.GetAttributeValue<OptionSetValue>("teamtype")?.Value ?? 0)
                });

            // Entity permissions (requires solution entity list)
            if (solutionEntities != null)
                detail.EntityPermissions.AddRange(FetchEntityPermissions(roleId, solutionEntities));

            return detail;
        }

        // ── Bulk entity permissions for ALL roles (one FetchXml call) ───────────
        // Used by documentation generation to avoid N per-role calls.
        public Dictionary<Guid, List<EntityPermission>> GetAllEntityPermissions(
            IEnumerable<Guid> roleIds, IEnumerable<EntityInfo> solutionEntities)
        {
            var roleIdList = roleIds.ToList();
            if (roleIdList.Count == 0) return new Dictionary<Guid, List<EntityPermission>>();

            var entityLookup = solutionEntities.ToDictionary(
                e => e.LogicalName,
                e => (logName: e.LogicalName, display: e.DisplayName),
                StringComparer.OrdinalIgnoreCase);

            if (entityLookup.Count == 0) return new Dictionary<Guid, List<EntityPermission>>();

            var roleValues = string.Join("",
                roleIdList.Select(id => $"<value>{id}</value>"));

            var fetchXml = $@"
<fetch>
  <entity name='privilege'>
    <attribute name='name'/>
    <link-entity name='roleprivileges' from='privilegeid' to='privilegeid'
                 link-type='inner' intersect='true' alias='rp'>
      <attribute name='privilegedepthmask'/>
      <attribute name='roleid'/>
      <filter>
        <condition attribute='roleid' operator='in'>
          {roleValues}
        </condition>
      </filter>
    </link-entity>
  </entity>
</fetch>";

            var rows = _service.RetrieveMultiple(new FetchExpression(fetchXml)).Entities;
            var byRole = new Dictionary<Guid, Dictionary<string, EntityPermission>>();

            foreach (var row in rows)
            {
                var privName = row.GetAttributeValue<string>("name") ?? "";
                var depthRaw = row.GetAttributeValue<AliasedValue>("rp.privilegedepthmask")?.Value;
                var mask = depthRaw is int di ? di : depthRaw != null ? Convert.ToInt32(depthRaw) : 0;

                var roleIdRaw = row.GetAttributeValue<AliasedValue>("rp.roleid")?.Value;
                var roleId = roleIdRaw is Guid g ? g
                           : roleIdRaw is EntityReference er ? er.Id
                           : Guid.Empty;
                if (roleId == Guid.Empty) continue;

                var (action, entityKey) = ParsePrivilegeName(privName);
                if (action == null || entityKey == null) continue;
                if (!entityLookup.TryGetValue(entityKey, out var info)) continue;

                var level = DepthMaskToLevel(mask);
                if (!byRole.TryGetValue(roleId, out var permMap))
                    byRole[roleId] = permMap = new Dictionary<string, EntityPermission>(StringComparer.OrdinalIgnoreCase);
                if (!permMap.TryGetValue(info.logName, out var perm))
                    permMap[info.logName] = perm = new EntityPermission
                    { EntityLogicalName = info.logName, EntityDisplayName = info.display };

                switch (action)
                {
                    case "Read":     perm.Read     = level; break;
                    case "Create":   perm.Create   = level; break;
                    case "Write":    perm.Write    = level; break;
                    case "Delete":   perm.Delete   = level; break;
                    case "Append":   perm.Append   = level; break;
                    case "AppendTo": perm.AppendTo = level; break;
                    case "Assign":   perm.Assign   = level; break;
                    case "Share":    perm.Share    = level; break;
                }
            }

            return byRole.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Values.OrderBy(p => p.EntityDisplayName).ToList());
        }

        // ── Entity permissions ────────────────────────────────────────────────
        // One FetchXml call traverses privilege ← roleprivileges → role and pulls
        // privilegedepthmask (bitmask: 1=User, 2=BU, 4=Deep, 8=Org) directly.
        private List<EntityPermission> FetchEntityPermissions(
            Guid roleId, IEnumerable<EntityInfo> solutionEntities)
        {
            var entityLookup = solutionEntities.ToDictionary(
                e => e.LogicalName,
                e => (logName: e.LogicalName, display: e.DisplayName),
                StringComparer.OrdinalIgnoreCase);

            if (entityLookup.Count == 0) return new List<EntityPermission>();

            var fetchXml = $@"
<fetch>
  <entity name='privilege'>
    <attribute name='name'/>
    <link-entity name='roleprivileges' from='privilegeid' to='privilegeid'
                 link-type='inner' intersect='true' alias='rp'>
      <attribute name='privilegedepthmask'/>
      <filter>
        <condition attribute='roleid' operator='eq' value='{roleId}'/>
      </filter>
    </link-entity>
  </entity>
</fetch>";

            var rows = _service.RetrieveMultiple(new FetchExpression(fetchXml)).Entities;
            var permMap = new Dictionary<string, EntityPermission>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                var privName = row.GetAttributeValue<string>("name") ?? "";
                var depthRaw = row.GetAttributeValue<AliasedValue>("rp.privilegedepthmask")?.Value;
                var mask = depthRaw is int di ? di
                         : depthRaw != null ? Convert.ToInt32(depthRaw) : 0;

                var (action, entityKey) = ParsePrivilegeName(privName);
                if (action == null || entityKey == null) continue;
                if (!entityLookup.TryGetValue(entityKey, out var info)) continue;

                var level = DepthMaskToLevel(mask);

                if (!permMap.TryGetValue(info.logName, out var perm))
                {
                    perm = new EntityPermission
                    {
                        EntityLogicalName = info.logName,
                        EntityDisplayName = info.display
                    };
                    permMap[info.logName] = perm;
                }

                switch (action)
                {
                    case "Read":     perm.Read     = level; break;
                    case "Create":   perm.Create   = level; break;
                    case "Write":    perm.Write    = level; break;
                    case "Delete":   perm.Delete   = level; break;
                    case "Append":   perm.Append   = level; break;
                    case "AppendTo": perm.AppendTo = level; break;
                    case "Assign":   perm.Assign   = level; break;
                    case "Share":    perm.Share    = level; break;
                }
            }

            return permMap.Values.OrderBy(p => p.EntityDisplayName).ToList();
        }

        // "prvAppendToAccount" → ("AppendTo", "Account")
        // "prvReadpw_Project"  → ("Read",     "pw_Project") — matches "pw_project" case-insensitively
        private static (string? action, string? entityKey) ParsePrivilegeName(string name)
        {
            if (!name.StartsWith("prv", StringComparison.OrdinalIgnoreCase))
                return (null, null);
            var rest = name.Substring(3);
            foreach (var action in new[] { "AppendTo", "Append", "Read", "Write", "Create", "Delete", "Assign", "Share" })
            {
                if (rest.StartsWith(action, StringComparison.OrdinalIgnoreCase))
                    return (action, rest.Substring(action.Length));
            }
            return (null, null);
        }

        // privilegedepthmask bitmask from roleprivileges intersect:
        //   1 = User/Basic, 2 = Business Unit, 4 = Deep (BU+children), 8 = Org/Global
        private static string DepthMaskToLevel(int mask)
        {
            if ((mask & 8) != 0) return "org";
            if ((mask & 4) != 0) return "deep";
            if ((mask & 2) != 0) return "bu";
            if ((mask & 1) != 0) return "user";
            return "none";
        }

        private static string TeamTypeName(int t) => t switch
        {
            0 => "Owner",
            1 => "Access",
            2 => "AAD Security Group",
            3 => "AAD Office Group",
            _ => "Unknown"
        };

        // ── JSON ─────────────────────────────────────────────────────────────
        public static string ToJson(RoleDetail d)
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"name\":{Js(d.Name)},\"businessUnit\":{Js(d.BusinessUnit)},");

            sb.Append("\"users\":[");
            for (int i = 0; i < d.Users.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var u = d.Users[i];
                sb.Append($"{{\"name\":{Js(u.Name)},\"email\":{Js(u.Email)},\"businessUnit\":{Js(u.BusinessUnit)}}}");
            }

            sb.Append("],\"teams\":[");
            for (int i = 0; i < d.Teams.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var t = d.Teams[i];
                sb.Append($"{{\"name\":{Js(t.Name)},\"businessUnit\":{Js(t.BusinessUnit)},\"teamType\":{Js(t.TeamType)}}}");
            }

            sb.Append("],\"entityPermissions\":[");
            for (int i = 0; i < d.EntityPermissions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = d.EntityPermissions[i];
                sb.Append($"{{\"logicalName\":{Js(p.EntityLogicalName)},\"displayName\":{Js(p.EntityDisplayName)}," +
                           $"\"create\":{Js(p.Create)},\"read\":{Js(p.Read)},\"write\":{Js(p.Write)}," +
                           $"\"delete\":{Js(p.Delete)},\"append\":{Js(p.Append)},\"appendTo\":{Js(p.AppendTo)}," +
                           $"\"assign\":{Js(p.Assign)},\"share\":{Js(p.Share)}}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Js(string? s) => s == null ? "null"
            : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }

    internal class RoleSummary
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string BusinessUnit { get; set; } = "";
        public override string ToString() =>
            string.IsNullOrEmpty(BusinessUnit) ? Name : $"{Name}  ·  {BusinessUnit}";
    }

    internal class RoleDetail
    {
        public string Name { get; set; } = "";
        public string BusinessUnit { get; set; } = "";
        public List<RoleUser> Users { get; } = new();
        public List<RoleTeam> Teams { get; } = new();
        public List<EntityPermission> EntityPermissions { get; } = new();
    }

    internal class RoleUser
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string BusinessUnit { get; set; } = "";
    }

    internal class RoleTeam
    {
        public string Name { get; set; } = "";
        public string BusinessUnit { get; set; } = "";
        public string TeamType { get; set; } = "";
    }

    internal class EntityPermission
    {
        public string EntityLogicalName { get; set; } = "";
        public string EntityDisplayName { get; set; } = "";
        public string Create   { get; set; } = "none";
        public string Read     { get; set; } = "none";
        public string Write    { get; set; } = "none";
        public string Delete   { get; set; } = "none";
        public string Append   { get; set; } = "none";
        public string AppendTo { get; set; } = "none";
        public string Assign   { get; set; } = "none";
        public string Share    { get; set; } = "none";
    }
}
