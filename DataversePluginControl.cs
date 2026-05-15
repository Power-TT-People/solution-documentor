using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Xrm.Sdk;
using XrmToolBox.Extensibility;

namespace XrmDataversePlugin
{
    public partial class DataversePluginControl : PluginControlBase
    {
        private ErdDataService? _erdSvc;
        private OptionSetService? _osSvc;
        private SecurityRoleService? _roleSvc;
        private FlowDocService? _flowSvc;
        private string? _lastMarkdown;
        private Dictionary<string, List<OptionSetUsage>> _osUsageMap = new();

        // Track which HTML is loaded and what to post when ready
        private enum HtmlMode { None, Erd, Doc }
        private HtmlMode _currentMode = HtmlMode.None;
        private bool _webViewReady;
        private Action? _onReady;
        private bool _isDark = true;

        // Cache last-posted JSON so switching tabs restores the view
        private string? _lastErdJson;
        private string? _lastDocJson;

        // Solution entity names, shared across tabs for option set usage lookup
        private List<string> _solutionEntityNames = new();

        // Track which tabs have had their list loaded for the current solution
        private bool _optionSetsLoaded;
        private bool _rolesLoaded;
        private bool _flowsLoaded;
        private bool _docLoaded;

        public DataversePluginControl()
        {
            InitializeComponent();
        }

        private void DataversePluginControl_Load(object sender, EventArgs e)
        {
            _ = InitWebViewAsync();
        }

        // ── WebView2 ─────────────────────────────────────────────────────────
        private async Task InitWebViewAsync()
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(
                    null, Path.Combine(Path.GetTempPath(), "XrmErdPluginWebView"));
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.WebMessageReceived += (s, ev) => { /* reserved */ };
                SwitchMode(HtmlMode.Erd);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 could not initialise:\n{ex.Message}",
                    "WebView2 Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SwitchMode(HtmlMode mode)
        {
            if (_currentMode == mode && _webViewReady) return;
            _currentMode = mode;
            _webViewReady = false;

            var htmlName = mode == HtmlMode.Erd ? "ErdCanvas.html" : "DocViewer.html";
            webView.NavigateToString(LoadEmbeddedHtml(htmlName));

            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        }

        // ── Theme toggle ─────────────────────────────────────────────────────
        internal void ToggleTheme()
        {
            _isDark = !_isDark;
            tsbTheme.Text = _isDark ? "☀ Light" : "🌙 Dark";
            if (_webViewReady)
                PostRaw($"{{\"type\":\"setTheme\",\"theme\":\"{(_isDark ? "dark" : "light")}\"}}");
        }

        private void OnNavigationCompleted(object sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            _webViewReady = true;

            // Always apply current theme first so the page starts in the right mode
            PostRaw($"{{\"type\":\"setTheme\",\"theme\":\"{(_isDark ? "dark" : "light")}\"}}");

            // Restore last view when switching back to a tab
            if (_currentMode == HtmlMode.Erd && _lastErdJson != null)
                PostRaw(_lastErdJson);
            else if (_currentMode == HtmlMode.Doc && _lastDocJson != null)
                PostRaw(_lastDocJson);

            if (_onReady != null)
            {
                var pending = _onReady;
                _onReady = null;
                pending();
            }
        }

        private void PostWhenReady(string json)
        {
            if (_webViewReady) PostRaw(json);
            else _onReady = () => PostRaw(json);
        }

        private void PostRaw(string json)
        {
            if (webView.CoreWebView2 != null)
                webView.CoreWebView2.PostWebMessageAsString(json);
        }

        private static string LoadEmbeddedHtml(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream($"XrmDataversePlugin.{name}")
                ?? throw new InvalidOperationException($"{name} not found as embedded resource.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // ── Tab switching ────────────────────────────────────────────────────
        private void tabLeft_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabLeft.SelectedTab == tabERD)
            {
                SwitchMode(HtmlMode.Erd);
            }
            else if (tabLeft.SelectedTab == tabOptionSets)
            {
                SwitchMode(HtmlMode.Doc);
                if (!_optionSetsLoaded) LoadOptionSetList();
            }
            else if (tabLeft.SelectedTab == tabRoles)
            {
                SwitchMode(HtmlMode.Doc);
                if (!_rolesLoaded) LoadRoleList();
            }
            else if (tabLeft.SelectedTab == tabFlows)
            {
                SwitchMode(HtmlMode.Doc);
                if (!_flowsLoaded) LoadFlowList();
            }
            else if (tabLeft.SelectedTab == tabDocumentation)
            {
                SwitchMode(HtmlMode.Doc);
            }
        }

        // ── Connection ───────────────────────────────────────────────────────
        public override void UpdateConnection(
            IOrganizationService newService,
            ConnectionDetail detail,
            string actionName,
            object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);
            _erdSvc = new ErdDataService(newService);
            _osSvc = new OptionSetService(newService);
            _roleSvc = new SecurityRoleService(newService);
            _flowSvc = new FlowDocService(newService);

            // Reset per-solution state
            _optionSetsLoaded = false;
            _rolesLoaded = false;
            _flowsLoaded = false;
            _solutionEntityNames.Clear();
            _osUsageMap = new();
            _lastErdJson = null;
            _lastDocJson = null;

            lblStatus.Text = $"Connected: {detail?.OrganizationFriendlyName ?? "Unknown"}";
            LoadSolutions();
        }

        // ── Solutions ────────────────────────────────────────────────────────
        private void LoadSolutions()
        {
            cmbSolution.Items.Clear();
            lstEntities.Items.Clear();
            lstOptionSets.Items.Clear();
            lstRoles.Items.Clear();
            lstFlows.Items.Clear();
            btnGenerate.Enabled = false;
            _optionSetsLoaded = false;
            _rolesLoaded = false;
            _flowsLoaded = false;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading solutions…",
                Work = (_, args) => args.Result = _erdSvc!.GetSolutions(),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Load solutions"); return; }
                    var sols = (List<SolutionInfo>)args.Result;
                    cmbSolution.Items.AddRange(sols.Cast<object>().ToArray());
                    lblStatus.Text = $"{sols.Count} solutions loaded.";
                }
            });
        }

        private void cmbSolution_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbSolution.SelectedItem is not SolutionInfo sol) return;
            lstEntities.Items.Clear();
            lstOptionSets.Items.Clear();
            lstRoles.Items.Clear();
            lstFlows.Items.Clear();
            btnGenerate.Enabled = false;
            chkSelectAll.Checked = false;
            _optionSetsLoaded = false;
            _rolesLoaded = false;
            _flowsLoaded = false;
            _solutionEntityNames.Clear();
            _osUsageMap = new();

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading entities…",
                Work = (_, args) => args.Result = _erdSvc!.GetEntitiesInSolution(sol.Id),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Load entities"); return; }
                    var entities = (List<EntityInfo>)args.Result;
                    foreach (var ent in entities) lstEntities.Items.Add(ent, false);
                    _solutionEntityNames = entities.Select(e => e.LogicalName).ToList();
                    btnGenerate.Enabled = entities.Count > 0;
                    lblStatus.Text = $"{entities.Count} entities in solution.";

                    // If Option Sets tab is active, load it now
                    if (tabLeft.SelectedTab == tabOptionSets && !_optionSetsLoaded)
                        LoadOptionSetList();
                }
            });
        }

        // ── ERD tab ──────────────────────────────────────────────────────────
        private void chkSelectAll_CheckedChanged(object sender, EventArgs e)
        {
            for (int i = 0; i < lstEntities.Items.Count; i++)
                lstEntities.SetItemChecked(i, chkSelectAll.Checked);
        }

        private void btnGenerate_Click(object sender, EventArgs e)
        {
            var selected = lstEntities.CheckedItems.Cast<EntityInfo>()
                .Select(ei => ei.LogicalName).ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show("Check at least one entity.",
                    "No selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnGenerate.Enabled = false;
            lblStatus.Text = $"Building ERD for {selected.Count} entities…";

            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Fetching metadata for {selected.Count} entities…",
                Work = (_, args) =>
                {
                    var schema = _erdSvc!.BuildSchema(selected);
                    args.Result = $"{{\"type\":\"loadSchema\",\"data\":{ErdDataService.ToJson(schema)}}}";
                },
                PostWorkCallBack = args =>
                {
                    btnGenerate.Enabled = true;
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Build ERD"); return; }
                    _lastErdJson = (string)args.Result;
                    lblStatus.Text = "ERD generated.";
                    SwitchMode(HtmlMode.Erd);
                    PostWhenReady(_lastErdJson);
                }
            });
        }

        // ── Option Sets tab ──────────────────────────────────────────────────
        private void LoadOptionSetList()
        {
            if (cmbSolution.SelectedItem is not SolutionInfo sol) return;
            _optionSetsLoaded = true;
            lstOptionSets.Items.Clear();
            lblStatus.Text = "Loading option sets…";

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading option sets…",
                Work = (_, args) => args.Result = _osSvc!.GetAllOptionSetsForTab(sol.Id),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Load option sets"); return; }
                    var loaded = (OptionSetLoadResult)args.Result;
                    _osUsageMap = loaded.UsageMap;
                    foreach (var os in loaded.Summaries) lstOptionSets.Items.Add(os);
                    var inSol = loaded.Summaries.Count(s => s.Scope == OptionSetScope.GlobalInSolution);
                    var extra = loaded.Summaries.Count - inSol;
                    lblStatus.Text = extra > 0
                        ? $"{inSol} in solution · {extra} additional (locals/externals)"
                        : $"{inSol} option sets in solution.";
                }
            });
        }

        private void lstOptionSets_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstOptionSets.SelectedItem is not OptionSetSummary summary) return;
            lblStatus.Text = $"Loading {summary.DisplayName}…";

            // Inline path — all data pre-fetched, zero API calls
            if (summary.InlineMetadata != null)
            {
                var detail = OptionSetService.GetDetailFromInline(summary.InlineMetadata, summary.Scope, _osUsageMap);
                var json = $"{{\"type\":\"loadOptionSet\",\"data\":{OptionSetService.ToJson(detail)}}}";
                _lastDocJson = json;
                PostWhenReady(json);
                lblStatus.Text = $"{summary.DisplayName} loaded.";
                return;
            }

            // Fallback for any item without pre-fetched data
            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Loading {summary.DisplayName}…",
                Work = (_, args) =>
                {
                    var detail = _osSvc!.GetDetail(summary.Name, _solutionEntityNames);
                    args.Result = $"{{\"type\":\"loadOptionSet\",\"data\":{OptionSetService.ToJson(detail)}}}";
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Load option set detail"); return; }
                    _lastDocJson = (string)args.Result;
                    PostWhenReady(_lastDocJson);
                    lblStatus.Text = $"{summary.DisplayName} loaded.";
                }
            });
        }

        // ── Security Roles tab ───────────────────────────────────────────────
        private void LoadRoleList()
        {
            if (cmbSolution.SelectedItem is not SolutionInfo sol) return;
            _rolesLoaded = true;
            lstRoles.Items.Clear();
            lblStatus.Text = "Loading security roles…";

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading security roles…",
                Work = (_, args) => args.Result = _roleSvc!.GetRolesInSolution(sol.Id),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Load roles"); return; }
                    var list = (List<RoleSummary>)args.Result;
                    foreach (var r in list) lstRoles.Items.Add(r);
                    lblStatus.Text = $"{list.Count} security roles in solution.";
                }
            });
        }

        private void lstRoles_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstRoles.SelectedItem is not RoleSummary role) return;
            lblStatus.Text = $"Loading {role.Name}…";

            // Capture entity infos on UI thread before background work starts
            var entityInfos = lstEntities.Items.Cast<EntityInfo>().ToList();

            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Loading {role.Name}…",
                AsyncArgument = entityInfos,
                Work = (_, args) =>
                {
                    var entities = (List<EntityInfo>)args.Argument;
                    var detail = _roleSvc!.GetDetail(role.Id, role.Name, role.BusinessUnit, entities);
                    args.Result = $"{{\"type\":\"loadRole\",\"data\":{SecurityRoleService.ToJson(detail)}}}";
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Load role detail"); return; }
                    _lastDocJson = (string)args.Result;
                    PostWhenReady(_lastDocJson);
                    lblStatus.Text = $"{role.Name} loaded.";
                }
            });
        }

        // ── Flows tab ────────────────────────────────────────────────────────
        private void LoadFlowList()
        {
            if (cmbSolution.SelectedItem is not SolutionInfo sol) return;
            _flowsLoaded = true;
            lstFlows.Items.Clear();
            lblStatus.Text = "Loading flows…";

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading flows…",
                Work = (_, args) => args.Result = _flowSvc!.GetFlowsInSolution(sol.Id),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Load flows"); return; }
                    var list = (List<FlowSummary>)args.Result;
                    foreach (var f in list) lstFlows.Items.Add(f);
                    lblStatus.Text = list.Count == 0
                        ? "No modern flows found in this solution."
                        : $"{list.Count} flow{(list.Count == 1 ? "" : "s")} in solution.";
                }
            });
        }

        private void lstFlows_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstFlows.SelectedItem is not FlowSummary flow) return;
            lblStatus.Text = $"Loading {flow.Name}…";

            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Parsing {flow.Name}…",
                Work = (_, args) =>
                {
                    var detail = _flowSvc!.GetDetail(flow);
                    args.Result = $"{{\"type\":\"loadFlow\",\"data\":{FlowDocService.ToJson(detail)}}}";
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Load flow detail"); return; }
                    _lastDocJson = (string)args.Result;
                    PostWhenReady(_lastDocJson);
                    lblStatus.Text = $"{flow.Name} loaded.";
                }
            });
        }

        // ── Documentation tab ────────────────────────────────────────────────
        private void btnGenerateDoc_Click(object sender, EventArgs e)
        {
            if (Service == null)
            {
                MessageBox.Show("Please connect to a Dataverse environment first.",
                    "Not connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (cmbSolution.SelectedItem is not SolutionInfo sol) return;

            btnGenerateDoc.Enabled = false;
            tsbSaveDoc.Enabled = false;
            lblStatus.Text = "Generating documentation…";

            var opts = new DocOptions
            {
                IncludeTables = chkDocTables.Checked,
                IncludeOptionSets = chkDocOptionSets.Checked,
                IncludeRoles = chkDocRoles.Checked,
                IncludeFlows = chkDocFlows.Checked
            };

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Generating documentation (this may take a while)…",
                Work = (worker, args) =>
                {
                    var docSvc = new DocumentationService(Service);
                    args.Result = docSvc.Generate(sol.Id, sol.FriendlyName, opts,
                        msg => worker.ReportProgress(0, msg));
                },
                ProgressChanged = ev => lblStatus.Text = ev.UserState?.ToString() ?? "",
                PostWorkCallBack = args =>
                {
                    btnGenerateDoc.Enabled = true;
                    if (args.Error != null) { ShowErrorDialog(args.Error, "Generate documentation"); return; }

                    _lastMarkdown = (string)args.Result;
                    tsbSaveDoc.Enabled = true;

                    var wordCount = _lastMarkdown.Split(new[] { ' ', '\n', '\r' },
                        StringSplitOptions.RemoveEmptyEntries).Length;
                    var stats = $"~{wordCount:N0} words";

                    // Escape markdown for JSON embedding
                    var jsonMd = _lastMarkdown
                        .Replace("\\", "\\\\")
                        .Replace("\"", "\\\"")
                        .Replace("\r", "")
                        .Replace("\n", "\\n")
                        .Replace("\t", "\\t");

                    var msg = $"{{\"type\":\"loadDocumentation\",\"data\":{{\"markdown\":\"{jsonMd}\",\"stats\":\"{stats}\"}}}}";
                    _lastDocJson = msg;
                    SwitchMode(HtmlMode.Doc);
                    PostWhenReady(msg);
                    lblStatus.Text = $"Documentation ready — {stats}";
                }
            });
        }

        internal void SaveDoc()
        {
            if (string.IsNullOrEmpty(_lastMarkdown)) return;
            var solutionName = (cmbSolution.SelectedItem as SolutionInfo)?.UniqueName ?? "solution";
            using var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Save documentation",
                FileName = $"{solutionName}_documentation_{DateTime.Now:yyyyMMdd}.md",
                Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                DefaultExt = "md"
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            System.IO.File.WriteAllText(dlg.FileName, _lastMarkdown, System.Text.Encoding.UTF8);
            lblStatus.Text = $"Saved: {System.IO.Path.GetFileName(dlg.FileName)}";
        }

        // ── Close ────────────────────────────────────────────────────────────
        private void tsbClose_Click(object sender, EventArgs e) => CloseTool();
    }
}
