using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;

namespace XrmDataversePlugin
{
    internal class FlowDocService
    {
        private readonly IOrganizationService _service;

        public FlowDocService(IOrganizationService service) => _service = service;

        // ── Flows by IDs (no solutioncomponent query — caller already has IDs) ─
        public List<FlowSummary> GetFlowsByIds(List<Guid> flowIds)
        {
            if (flowIds.Count == 0) return new List<FlowSummary>();

            var q = new QueryExpression("workflow")
            {
                ColumnSet = new ColumnSet("workflowid", "name", "statecode", "description")
            };
            q.Criteria.AddCondition("workflowid", ConditionOperator.In, flowIds.Cast<object>().ToArray());
            q.Criteria.AddCondition("category", ConditionOperator.Equal, 5); // Modern Flow
            q.AddOrder("name", OrderType.Ascending);

            return _service.RetrieveMultiple(q).Entities
                .Select(e => new FlowSummary
                {
                    Id = e.GetAttributeValue<Guid>("workflowid"),
                    Name = e.GetAttributeValue<string>("name") ?? "",
                    IsActive = e.GetAttributeValue<OptionSetValue>("statecode")?.Value == 1,
                    Description = e.GetAttributeValue<string>("description") ?? ""
                })
                .ToList();
        }

        // ── Flows in solution ────────────────────────────────────────────────
        public List<FlowSummary> GetFlowsInSolution(Guid solutionId)
        {
            // componenttype 29 = Workflow/Process (covers all flow types)
            var compQ = new QueryExpression("solutioncomponent")
            {
                ColumnSet = new ColumnSet("objectid")
            };
            compQ.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            compQ.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 29);

            var ids = _service.RetrieveMultiple(compQ).Entities
                .Select(e => e.GetAttributeValue<Guid>("objectid"))
                .ToList();

            if (ids.Count == 0) return new List<FlowSummary>();

            var q = new QueryExpression("workflow")
            {
                ColumnSet = new ColumnSet("workflowid", "name", "statecode", "description")
            };
            q.Criteria.AddCondition("workflowid", ConditionOperator.In, ids.Cast<object>().ToArray());
            q.Criteria.AddCondition("category", ConditionOperator.Equal, 5); // Modern Flow
            q.AddOrder("name", OrderType.Ascending);

            return _service.RetrieveMultiple(q).Entities
                .Select(e => new FlowSummary
                {
                    Id = e.GetAttributeValue<Guid>("workflowid"),
                    Name = e.GetAttributeValue<string>("name") ?? "",
                    IsActive = e.GetAttributeValue<OptionSetValue>("statecode")?.Value == 1,
                    Description = e.GetAttributeValue<string>("description") ?? ""
                })
                .ToList();
        }

        // ── Bulk detail fetch (chunked ExecuteMultiple for clientdata) ───────
        public Dictionary<Guid, FlowDetail> GetAllDetails(List<FlowSummary> flows)
        {
            const int chunkSize = 15; // clientdata strings can be large
            var result = new Dictionary<Guid, FlowDetail>();

            for (int i = 0; i < flows.Count; i += chunkSize)
            {
                var chunk = flows.Skip(i).Take(chunkSize).ToList();
                var batch = new ExecuteMultipleRequest
                {
                    Requests = new OrganizationRequestCollection(),
                    Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true }
                };
                foreach (var f in chunk)
                    batch.Requests.Add(new RetrieveRequest
                    {
                        Target = new EntityReference("workflow", f.Id),
                        ColumnSet = new ColumnSet("clientdata", "description")
                    });

                var resp = (ExecuteMultipleResponse)_service.Execute(batch);
                for (int j = 0; j < resp.Responses.Count; j++)
                {
                    var r = resp.Responses[j];
                    if (r.Fault != null || r.Response == null) continue;
                    var flow = chunk[j];
                    var record = ((RetrieveResponse)r.Response).Entity;
                    var clientData = record.GetAttributeValue<string>("clientdata") ?? "{}";
                    var description = record.GetAttributeValue<string>("description") ?? flow.Description;
                    result[flow.Id] = ParseFlow(flow.Name, flow.IsActive, description, clientData);
                }
            }

            return result;
        }

        // ── Flow detail ──────────────────────────────────────────────────────
        public FlowDetail GetDetail(FlowSummary summary)
        {
            var record = _service.Retrieve("workflow", summary.Id,
                new ColumnSet("clientdata", "description"));

            var clientData = record.GetAttributeValue<string>("clientdata") ?? "{}";
            var description = record.GetAttributeValue<string>("description")
                ?? summary.Description;

            return ParseFlow(summary.Name, summary.IsActive, description, clientData);
        }

        // ── Parser ───────────────────────────────────────────────────────────
        private FlowDetail ParseFlow(string name, bool isActive, string description, string clientData)
        {
            var detail = new FlowDetail { Name = name, IsActive = isActive, Description = description };

            try
            {
                var root = JObject.Parse(clientData);
                var def = root["properties"]?["definition"];
                var connRefs = root["properties"]?["connectionReferences"] as JObject;

                if (def == null) return detail;

                // Trigger
                if (def["triggers"] is JObject triggers && triggers.Count > 0)
                {
                    var t = triggers.Properties().First();
                    detail.Trigger = ParseTrigger(t.Name, t.Value as JObject, connRefs);
                }

                // Actions (topological order → flat list with indent)
                if (def["actions"] is JObject actions)
                    WalkActions(actions, connRefs, detail.Steps, 0, new Counter());
            }
            catch (Exception ex)
            {
                detail.ParseError = true;
                detail.ParseErrorMessage = ex.GetType().Name + ": " + ex.Message;
            }

            return detail;
        }

        // ── Trigger ──────────────────────────────────────────────────────────
        private FlowTrigger ParseTrigger(string rawName, JObject? t, JObject? connRefs)
        {
            if (t == null) return new FlowTrigger { Name = Fmt(rawName), Description = "Unknown trigger" };

            var type = t["type"]?.ToString() ?? "";
            var kind = t["kind"]?.ToString() ?? "";

            string desc = type switch
            {
                "Recurrence" => $"Runs on a schedule ({RecurrenceText(t)})",
                "Request" when kind == "Button" => "Triggered manually (button)",
                "Request" when kind == "PowerApp" => "Triggered from Power Apps",
                "Request" => "Triggered by an HTTP request",
                "ApiConnectionWebhook" or "ApiConnection" => ConnTriggerText(t, connRefs),
                "Manual" => "Triggered manually",
                _ => $"Triggered by {type}"
            };

            return new FlowTrigger { Name = Fmt(rawName), Type = type, Description = desc };
        }

        private string RecurrenceText(JObject t)
        {
            var r = t["recurrence"] ?? t["inputs"]?["recurrence"];
            if (r == null) return "scheduled";
            return $"every {r["interval"]} {r["frequency"]?.ToString()?.ToLower()}(s)";
        }

        private string ConnTriggerText(JObject t, JObject? connRefs)
        {
            var host = t["inputs"]?["host"];
            var conn = host?["connectionName"]?.ToString() ?? "";
            var opId = host?["operationId"]?.ToString() ?? t["inputs"]?["operationId"]?.ToString() ?? "";
            var display = ConnDisplay(conn, connRefs);

            if (opId.Contains("record_is_created") || (opId.Contains("SubscribeWebhook") && display.Contains("ataverse")))
                return "Triggers when a Dataverse record is created";
            if (opId.Contains("record_is_updated")) return "Triggers when a Dataverse record is updated";
            if (opId.Contains("record_is_deleted")) return "Triggers when a Dataverse record is deleted";
            if (opId.Contains("record_is_created_updated_or_deleted")) return "Triggers when a Dataverse record changes";
            if (opId.Contains("NewEmail") || opId.Contains("new_email")) return "Triggers when a new email arrives";
            if (opId.Contains("new_item") || opId.Contains("new_file")) return $"Triggers when a new {display} item is created";
            return display.Length > 0 ? $"Triggered by {display}" : $"Triggered by {opId}";
        }

        // ── Action walker ────────────────────────────────────────────────────
        private void WalkActions(JObject actions, JObject? connRefs,
            List<FlowStep> steps, int indent, Counter counter)
        {
            foreach (var (aName, action) in TopoSort(actions))
            {
                if (action == null) continue;

                var step = BuildStep(aName, action, connRefs, indent, ++counter.Value);
                steps.Add(step);

                var type = (action["type"]?.ToString() ?? "").ToLower();

                switch (type)
                {
                    case "if":
                    case "condition":
                        AddBranch(action["actions"] as JObject, "TRUE",  "#22c55e", connRefs, steps, indent + 1, counter);
                        AddBranch(action["else"]?["actions"] as JObject, "FALSE", "#f87171", connRefs, steps, indent + 1, counter);
                        break;

                    case "foreach":
                    case "apply_to_each":
                        WalkActions(action["actions"] as JObject ?? new JObject(), connRefs, steps, indent + 1, counter);
                        break;

                    case "scope":
                        WalkActions(action["actions"] as JObject ?? new JObject(), connRefs, steps, indent + 1, counter);
                        break;

                    case "switch":
                        if (action["cases"] is JObject cases)
                            foreach (var c in cases.Properties())
                            {
                                var caseVal = (c.Value as JObject)?["case"]?.ToString() ?? c.Name;
                                steps.Add(BranchLabel($"Case: {caseVal}", "#f59e0b", indent + 1));
                                WalkActions((c.Value as JObject)?["actions"] as JObject ?? new JObject(), connRefs, steps, indent + 2, counter);
                            }
                        if (action["default"]?["actions"] is JObject defaultActs && defaultActs.Count > 0)
                        {
                            steps.Add(BranchLabel("Default", "#64748b", indent + 1));
                            WalkActions(defaultActs, connRefs, steps, indent + 2, counter);
                        }
                        break;

                    case "until":
                    case "do_until":
                        WalkActions(action["actions"] as JObject ?? new JObject(), connRefs, steps, indent + 1, counter);
                        break;
                }
            }
        }

        private void AddBranch(JObject? branchActions, string label, string color,
            JObject? connRefs, List<FlowStep> steps, int indent, Counter counter)
        {
            if (branchActions == null || branchActions.Count == 0) return;
            steps.Add(BranchLabel(label, color, indent));
            WalkActions(branchActions, connRefs, steps, indent + 1, counter);
        }

        private FlowStep BranchLabel(string label, string color, int indent) =>
            new FlowStep { Name = label, IsBranchLabel = true, BranchColor = color, Indent = indent };

        // ── Step builder ──────────────────────────────────────────────────────
        private FlowStep BuildStep(string rawName, JObject action, JObject? connRefs,
            int indent, int stepNum)
        {
            var type = action["type"]?.ToString() ?? "";
            var (badge, color, desc) = StepInfo(rawName, type, action, connRefs);
            var step = new FlowStep
            {
                Id = rawName,
                Name = Fmt(rawName),
                Type = type,
                Badge = badge,
                BadgeColor = color,
                Description = desc,
                Indent = indent,
                StepNumber = stepNum
            };
            step.Parameters.AddRange(ExtractParams(type, action));
            return step;
        }

        // ── Step info (badge + description) ───────────────────────────────────
        private (string badge, string color, string desc) StepInfo(
            string rawName, string type, JObject action, JObject? connRefs)
        {
            var inputs = action["inputs"] as JObject;

            switch (type.ToLower())
            {
                case "initializevariable":
                {
                    var v = action["inputs"]?["variables"]?[0];
                    return ("Variable", "#a78bfa",
                        $"Initialize {v?["type"]} variable '{v?["name"]}'");
                }
                case "setvariable":
                    return ("Variable", "#a78bfa", $"Set variable '{inputs?["name"]}'");
                case "appendtoarrayvariable":
                    return ("Variable", "#a78bfa", $"Append to array '{inputs?["name"]}'");
                case "appendtostringvariable":
                    return ("Variable", "#a78bfa", $"Append to string '{inputs?["name"]}'");
                case "incrementvariable":
                    return ("Variable", "#a78bfa", $"Increment '{inputs?["name"]}'");
                case "decrementvariable":
                    return ("Variable", "#a78bfa", $"Decrement '{inputs?["name"]}'");

                case "if":
                case "condition":
                    return ("Condition", "#f59e0b", "If " + ConditionText(action));

                case "foreach":
                case "apply_to_each":
                    return ("Loop", "#60a5fa", $"For each item in {Shorten(action["foreach"]?.ToString() ?? "")}");

                case "scope":
                    return ("Scope", "#64748b", $"Scope: {Fmt(rawName)}");

                case "switch":
                    return ("Switch", "#f59e0b", $"Switch on {Shorten(action["expression"]?.ToString() ?? "")}");

                case "until":
                case "do_until":
                    return ("Loop", "#60a5fa", "Do until condition is met");

                case "compose":
                    return ("Compose", "#94a3b8", "Compose expression");

                case "parsejson":
                    return ("Parse JSON", "#94a3b8", "Parse JSON content");

                case "select":
                    return ("Transform", "#94a3b8", "Select / map array items");

                case "filter":
                case "query":
                    return ("Transform", "#94a3b8", "Filter array");

                case "join":
                    return ("Transform", "#94a3b8", "Join array to string");

                case "http":
                {
                    var method = inputs?["method"]?.ToString() ?? "HTTP";
                    var uri = Shorten(inputs?["uri"]?.ToString() ?? "");
                    return ("HTTP", "#facc15", $"{method.ToUpper()} {uri}");
                }

                case "response":
                    return ("HTTP", "#facc15", "Send HTTP response");

                case "terminate":
                    return ("Terminate", "#f87171",
                        $"Terminate flow ({inputs?["runStatus"]})");

                case "delay":
                case "wait":
                {
                    var cnt = inputs?["interval"]?["count"]?.ToString() ?? "";
                    var unit = inputs?["interval"]?["unit"]?.ToString() ?? "";
                    return ("Wait", "#94a3b8", $"Wait {cnt} {unit}".Trim());
                }

                case "apiconnection":
                case "openapiconnection":
                    return ApiConnInfo(rawName, action, connRefs);

                case "apiconnectionwebhook":
                    return ("Webhook", "#22d3ee", "Wait for webhook event");

                case "workflowcall":
                case "workflow":
                    return ("Child Flow", "#a78bfa", "Run a child flow");

                case "sendnotification":
                    return ("Notification", "#34d399", "Send push notification");

                default:
                    return (type.Length > 16 ? type.Substring(0, 16) : type, "#475569", Fmt(rawName));
            }
        }

        private (string badge, string color, string desc) ApiConnInfo(
            string rawName, JObject action, JObject? connRefs)
        {
            var host = action["inputs"]?["host"] as JObject;
            var conn = host?["connectionName"]?.ToString() ?? host?["connection"]?["name"]?.ToString() ?? "";
            var opId = host?["operationId"]?.ToString() ?? action["inputs"]?["operationId"]?.ToString() ?? "";
            var parms = action["inputs"]?["parameters"] as JObject ?? action["inputs"]?["body"] as JObject;
            var display = ConnDisplay(conn, connRefs);

            // Dataverse
            if (conn.Contains("commondataservice") || conn.Contains("powerplatform") ||
                conn.Contains("cds") || display.ToLower().Contains("dataverse"))
            {
                var entity = parms?["entityName"]?.ToString() ?? parms?["table"]?.ToString() ?? "";
                var desc = opId.ToLower() switch
                {
                    var o when o.Contains("list") || o.Contains("getitems") => $"List {entity} records",
                    var o when o.Contains("getitem") || o.Contains("getrecord") => $"Get {entity} record",
                    var o when o.Contains("create") => $"Create {entity} record",
                    var o when o.Contains("update") => $"Update {entity} record",
                    var o when o.Contains("delete") => $"Delete {entity} record",
                    var o when o.Contains("executeaction") || o.Contains("performaction") => "Execute Dataverse action",
                    var o when o.Contains("fetchxml") => "Execute FetchXml query",
                    _ => $"Dataverse: {FmtOpId(opId)}"
                };
                return ("Dataverse", "#22d3ee", desc.Trim());
            }

            // Email / Outlook
            if (conn.Contains("office365") || conn.Contains("outlook") || conn.Contains("smtp"))
            {
                var desc = opId.ToLower() switch
                {
                    var o when o.Contains("sendemail") || o.Contains("send") => "Send email",
                    var o when o.Contains("reply") => "Reply to email",
                    var o when o.Contains("forward") => "Forward email",
                    _ => $"Outlook: {FmtOpId(opId)}"
                };
                return ("Email", "#60a5fa", desc);
            }

            // Teams
            if (conn.Contains("teams") || conn.Contains("microsoftteams"))
            {
                var desc = opId.ToLower() switch
                {
                    var o when o.Contains("post") => "Post Teams message",
                    var o when o.Contains("meeting") => "Create Teams meeting",
                    _ => $"Teams: {FmtOpId(opId)}"
                };
                return ("Teams", "#818cf8", desc);
            }

            // Approvals
            if (conn.Contains("approval"))
                return ("Approval", "#34d399", "Start and wait for approval");

            // SharePoint
            if (conn.Contains("sharepoint"))
                return ("SharePoint", "#22c55e", $"SharePoint: {FmtOpId(opId)}");

            // Generic
            var label = display.Length > 0 ? display : "Connector";
            if (label.Length > 14) label = label.Substring(0, 14);
            return (label, "#94a3b8", $"{display}: {FmtOpId(opId)}".Trim(' ', ':'));
        }

        private string ConnDisplay(string connName, JObject? connRefs)
        {
            if (connRefs == null || string.IsNullOrEmpty(connName)) return connName;
            var r = connRefs[connName];
            return r?["displayName"]?.ToString()
                ?? r?["connection"]?["displayName"]?.ToString()
                ?? connName;
        }

        private string ConditionText(JObject action)
        {
            var expr = action["expression"];
            if (expr is JValue) return Shorten(expr.ToString());
            if (expr is JObject eo)
            {
                var op = eo.Properties().FirstOrDefault();
                if (op != null && op.Value is JArray terms && terms.Count >= 2)
                {
                    var left = Shorten(terms[0]?.ToString() ?? "");
                    var right = Shorten(terms[1]?.ToString() ?? "");
                    var opLabel = op.Name.ToLower() switch
                    {
                        "equals" => "equals",
                        "not" => "does not equal",
                        "greater" => ">",
                        "less" => "<",
                        "greaterorequals" => "≥",
                        "lessorequals" => "≤",
                        "contains" => "contains",
                        "startswith" => "starts with",
                        "endswith" => "ends with",
                        _ => op.Name
                    };
                    return $"{left} {opLabel} {right}";
                }
            }
            return "condition";
        }

        // ── Parameter extraction ──────────────────────────────────────────────
        private static List<FlowParam> ExtractParams(string type, JObject action)
        {
            var result = new List<FlowParam>();
            try
            {
            var inputs = action["inputs"] as JObject;

            switch (type.ToLower())
            {
                case "initializevariable":
                    var v0 = inputs?["variables"]?[0] as JObject;
                    if (v0 != null)
                    {
                        Add(result, "Name", v0["name"]);
                        Add(result, "Type", v0["type"]);
                        Add(result, "Value", v0["value"]);
                    }
                    break;

                case "setvariable":
                case "appendtoarrayvariable":
                case "appendtostringvariable":
                case "incrementvariable":
                case "decrementvariable":
                    Add(result, "Name", inputs?["name"]);
                    Add(result, "Value", inputs?["value"]);
                    break;

                case "compose":
                    Add(result, "Value", inputs?["inputs"]);
                    break;

                case "parsejson":
                    Add(result, "Content", inputs?["content"]);
                    break;

                case "http":
                    Add(result, "Method", inputs?["method"]);
                    Add(result, "URL", inputs?["uri"]);
                    if (inputs?["body"] != null) Add(result, "Body", inputs["body"]);
                    if (inputs?["headers"] is JObject hdrs)
                        foreach (var h in hdrs.Properties())
                            Add(result, $"Header: {h.Name}", h.Value);
                    break;

                case "if":
                case "condition":
                    var exprToken = action["expression"];
                    if (exprToken != null)
                    {
                        var exprStr = exprToken.Type == JTokenType.String
                            ? exprToken.ToString()
                            : exprToken.ToString();
                        if (!string.IsNullOrEmpty(exprStr))
                            result.Add(new FlowParam { Key = "Expression", Value = exprStr });
                    }
                    break;

                case "foreach":
                case "apply_to_each":
                    Add(result, "Items", action["foreach"]);
                    break;

                case "until":
                case "do_until":
                    Add(result, "Expression", action["expression"]);
                    Add(result, "Limit (count)", action["limit"]?["count"]);
                    Add(result, "Limit (timeout)", action["limit"]?["timeout"]);
                    break;

                case "switch":
                    Add(result, "On", action["expression"]);
                    break;

                case "delay":
                case "wait":
                    Add(result, "Count", inputs?["interval"]?["count"]);
                    Add(result, "Unit", inputs?["interval"]?["unit"]);
                    break;

                case "response":
                    Add(result, "Status Code", inputs?["statusCode"]);
                    Add(result, "Body", inputs?["body"]);
                    break;

                case "terminate":
                    Add(result, "Status", inputs?["runStatus"]);
                    Add(result, "Code", inputs?["code"]);
                    Add(result, "Message", inputs?["message"]);
                    break;

                case "apiconnection":
                case "openapiconnection":
                    var parms = inputs?["parameters"] as JObject;
                    if (parms != null)
                    {
                        foreach (var p in parms.Properties())
                        {
                            if (SkipApiParam(p.Name)) continue;
                            var label = CleanParamName(p.Name);
                            Add(result, label, p.Value);
                        }
                    }
                    // Also check top-level inputs for things not in parameters
                    if (inputs != null)
                    {
                        foreach (var p in inputs.Properties())
                        {
                            if (p.Name == "host" || p.Name == "parameters" ||
                                p.Name == "authentication" || p.Name == "path") continue;
                            Add(result, p.Name, p.Value);
                        }
                    }
                    break;

                default:
                    if (inputs != null)
                    {
                        foreach (var p in inputs.Properties())
                        {
                            if (p.Name == "host" || p.Name == "authentication") continue;
                            Add(result, p.Name, p.Value);
                        }
                    }
                    break;
            }

            } catch { /* parameter extraction is best-effort; never break the main parse */ }
            return result;
        }

        private static bool SkipApiParam(string name)
        {
            return name == "$select" ||
                   name.StartsWith("api-") ||
                   name == "subscriptionId" ||
                   name == "resourceGroupName";
        }

        private static string CleanParamName(string name)
        {
            // Strip common prefixes: "item/", "emailMessage/", "message/"
            foreach (var prefix in new[] { "item/", "emailMessage/", "message/", "calendarEvent/" })
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(prefix.Length);
            return name;
        }

        private static void Add(List<FlowParam> list, string key, JToken? value)
        {
            if (value == null || value.Type == JTokenType.Null) return;

            string str;
            if (value.Type == JTokenType.Object)
            {
                // For objects, produce a compact one-line JSON (truncated)
                str = value.ToString();
            }
            else if (value.Type == JTokenType.Array)
            {
                var arr = (JArray)value;
                str = arr.Count == 0 ? "[ ]" : $"[ {arr.Count} item{(arr.Count == 1 ? "" : "s")} ]";
            }
            else
            {
                str = value.ToString().Trim();
            }

            if (string.IsNullOrEmpty(str)) return;

            list.Add(new FlowParam { Key = key, Value = str });
        }

        // ── Topological sort ──────────────────────────────────────────────────
        private static List<(string, JObject?)> TopoSort(JObject actions)
        {
            var result = new List<(string, JObject?)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in actions.Properties())
                TopoVisit(p.Name, actions, visited, active, result);

            return result;
        }

        private static void TopoVisit(string name, JObject actions,
            HashSet<string> visited, HashSet<string> active,
            List<(string, JObject?)> result)
        {
            if (visited.Contains(name) || active.Contains(name)) return;
            active.Add(name);

            var action = actions[name] as JObject;
            if (action?["runAfter"] is JObject ra)
                foreach (var dep in ra.Properties())
                    if (actions[dep.Name] != null)
                        TopoVisit(dep.Name, actions, visited, active, result);

            active.Remove(name);
            visited.Add(name);
            result.Add((name, action));
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string Fmt(string s) => s.Replace('_', ' ').Replace('-', ' ').Trim();

        private static string FmtOpId(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.TrimStart('@').Trim();
            return s.Length > 48 ? s.Substring(0, 45) + "…" : s;
        }

        // ── JSON ──────────────────────────────────────────────────────────────
        public static string ToJson(FlowDetail d)
        {
            var sb = new StringBuilder();
            sb.Append($"{{\"name\":{Js(d.Name)},\"description\":{Js(d.Description)}," +
                      $"\"isActive\":{B(d.IsActive)},\"parseError\":{B(d.ParseError)}," +
                      $"\"parseErrorMessage\":{Js(d.ParseErrorMessage)},");

            if (d.Trigger != null)
                sb.Append($"\"trigger\":{{\"name\":{Js(d.Trigger.Name)},\"type\":{Js(d.Trigger.Type)},\"description\":{Js(d.Trigger.Description)}}},");
            else
                sb.Append("\"trigger\":null,");

            sb.Append("\"steps\":[");
            for (int i = 0; i < d.Steps.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var s = d.Steps[i];
                var paramJson = string.Join(",", s.Parameters.Select(p =>
                    $"{{\"key\":{Js(p.Key)},\"value\":{Js(p.Value)}}}"));
                sb.Append($"{{\"id\":{Js(s.Id)},\"name\":{Js(s.Name)},\"type\":{Js(s.Type)}," +
                          $"\"badge\":{Js(s.Badge)},\"badgeColor\":{Js(s.BadgeColor)}," +
                          $"\"description\":{Js(s.Description)},\"indent\":{s.Indent}," +
                          $"\"stepNumber\":{s.StepNumber},\"isBranchLabel\":{B(s.IsBranchLabel)}," +
                          $"\"branchColor\":{Js(s.BranchColor)},\"params\":[{paramJson}]}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Js(string? s) => s == null ? "null"
            : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        private static string B(bool b) => b ? "true" : "false";
    }

    internal class Counter { public int Value; }

    internal class FlowSummary
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
        public string Description { get; set; } = "";
        public override string ToString() => IsActive ? $"● {Name}" : $"○ {Name}";
    }

    internal class FlowDetail
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsActive { get; set; }
        public bool ParseError { get; set; }
        public string ParseErrorMessage { get; set; } = "";
        public FlowTrigger? Trigger { get; set; }
        public List<FlowStep> Steps { get; set; } = new();
    }

    internal class FlowTrigger
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
    }

    internal class FlowStep
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Badge { get; set; } = "";
        public string BadgeColor { get; set; } = "#475569";
        public string Description { get; set; } = "";
        public int Indent { get; set; }
        public int StepNumber { get; set; }
        public bool IsBranchLabel { get; set; }
        public string? BranchColor { get; set; }
        public List<FlowParam> Parameters { get; } = new();
    }

    internal class FlowParam
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
