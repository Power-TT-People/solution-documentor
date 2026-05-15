using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace XrmDataversePlugin
{
    partial class DataversePluginControl
    {
        private System.ComponentModel.IContainer components = null;

        // Shared
        private ToolStrip toolStrip;
        private ToolStripButton tsbClose;
        private ToolStripSeparator tsSep;
        private ToolStripButton tsbTheme;
        private Panel pnlSolution;
        private Label lblSolution;
        private ComboBox cmbSolution;
        private SplitContainer splitMain;
        private Label lblStatus;
        private WebView2 webView;

        // Left tab control
        private TabControl tabLeft;
        private TabPage tabERD;
        private TabPage tabOptionSets;

        // ERD tab controls
        private CheckBox chkSelectAll;
        private CheckedListBox lstEntities;
        private Button btnGenerate;

        // Option Sets tab controls
        private ListBox lstOptionSets;

        // Security Roles tab controls
        private TabPage tabRoles;
        private ListBox lstRoles;

        // Flows tab controls
        private TabPage tabFlows;
        private ListBox lstFlows;

        // Documentation tab controls
        private TabPage tabDocumentation;
        private CheckBox chkDocTables;
        private CheckBox chkDocOptionSets;
        private CheckBox chkDocRoles;
        private CheckBox chkDocFlows;
        private Button btnGenerateDoc;
        private ToolStripSeparator tsSep2;
        private ToolStripButton tsbSaveDoc;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            // ── ToolStrip ──────────────────────────────────────────────────
            tsbClose = new ToolStripButton { Text = "Close" };
            tsbClose.Click += tsbClose_Click;

            tsSep = new ToolStripSeparator();

            tsbTheme = new ToolStripButton { Text = "☀ Light", ToolTipText = "Switch to light mode" };
            tsbTheme.Click += (s, ev) => ToggleTheme();

            tsSep2 = new ToolStripSeparator();

            tsbSaveDoc = new ToolStripButton { Text = "💾 Save .md", Enabled = false, ToolTipText = "Save documentation as Markdown file" };
            tsbSaveDoc.Click += (s, ev) => SaveDoc();

            toolStrip = new ToolStrip { Dock = DockStyle.Top };
            toolStrip.Items.Add(tsbClose);
            toolStrip.Items.Add(tsSep);
            toolStrip.Items.Add(tsbTheme);
            toolStrip.Items.Add(tsSep2);
            toolStrip.Items.Add(tsbSaveDoc);

            // ── Solution picker panel ──────────────────────────────────────
            lblSolution = new Label
            {
                Text = "Solution",
                AutoSize = true,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(8, 6)
            };
            cmbSolution = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(8, 22),
                Size = new Size(254, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9f)
            };
            cmbSolution.SelectedIndexChanged += cmbSolution_SelectedIndexChanged;

            pnlSolution = new Panel { Height = 54, Dock = DockStyle.Top, Padding = new Padding(0) };
            pnlSolution.Controls.AddRange(new Control[] { lblSolution, cmbSolution });

            // ── ERD tab ────────────────────────────────────────────────────
            chkSelectAll = new CheckBox
            {
                Text = "Select all",
                AutoSize = true,
                Location = new Point(4, 4),
                Font = new Font("Segoe UI", 8f)
            };
            chkSelectAll.CheckedChanged += chkSelectAll_CheckedChanged;

            lstEntities = new CheckedListBox
            {
                Location = new Point(4, 26),
                Size = new Size(246, 300),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                CheckOnClick = true,
                Font = new Font("Segoe UI", 8.5f),
                IntegralHeight = false
            };

            btnGenerate = new Button
            {
                Text = "Generate ERD",
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(4, 334),
                Size = new Size(246, 30),
                Enabled = false,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(8, 145, 178),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnGenerate.FlatAppearance.BorderSize = 0;
            btnGenerate.Click += btnGenerate_Click;

            tabERD = new TabPage { Text = "ERD", Padding = new Padding(0) };
            tabERD.Controls.AddRange(new Control[] { chkSelectAll, lstEntities, btnGenerate });

            // ── Option Sets tab ────────────────────────────────────────────
            lstOptionSets = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            lstOptionSets.SelectedIndexChanged += lstOptionSets_SelectedIndexChanged;

            tabOptionSets = new TabPage { Text = "Option Sets", Padding = new Padding(4) };
            tabOptionSets.Controls.Add(lstOptionSets);

            // ── Security Roles tab ─────────────────────────────────────────
            lstRoles = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            lstRoles.SelectedIndexChanged += lstRoles_SelectedIndexChanged;

            tabRoles = new TabPage { Text = "Security Roles", Padding = new Padding(4) };
            tabRoles.Controls.Add(lstRoles);

            // ── Flows tab ──────────────────────────────────────────────────
            lstFlows = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                BorderStyle = BorderStyle.None,
                IntegralHeight = false
            };
            lstFlows.SelectedIndexChanged += lstFlows_SelectedIndexChanged;

            tabFlows = new TabPage { Text = "Flows", Padding = new Padding(4) };
            tabFlows.Controls.Add(lstFlows);

            // ── Documentation tab ──────────────────────────────────────────
            int cy = 8;
            chkDocTables = new CheckBox { Text = "Tables & Columns", Checked = true, AutoSize = true, Location = new Point(8, cy), Font = new Font("Segoe UI", 8.5f) }; cy += 24;
            chkDocOptionSets = new CheckBox { Text = "Option Sets", Checked = true, AutoSize = true, Location = new Point(8, cy), Font = new Font("Segoe UI", 8.5f) }; cy += 24;
            chkDocRoles = new CheckBox { Text = "Security Roles", Checked = true, AutoSize = true, Location = new Point(8, cy), Font = new Font("Segoe UI", 8.5f) }; cy += 24;
            chkDocFlows = new CheckBox { Text = "Flows", Checked = true, AutoSize = true, Location = new Point(8, cy), Font = new Font("Segoe UI", 8.5f) }; cy += 32;

            btnGenerateDoc = new Button
            {
                Text = "Generate Documentation",
                Location = new Point(8, cy),
                Size = new Size(246, 32),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(8, 145, 178),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnGenerateDoc.FlatAppearance.BorderSize = 0;
            btnGenerateDoc.Click += btnGenerateDoc_Click;

            tabDocumentation = new TabPage { Text = "📄 Docs", Padding = new Padding(4) };
            tabDocumentation.Controls.AddRange(new Control[] {
                chkDocTables, chkDocOptionSets, chkDocRoles, chkDocFlows, btnGenerateDoc
            });

            // ── Tab control ────────────────────────────────────────────────
            tabLeft = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                Padding = new Point(10, 4)
            };
            tabLeft.TabPages.AddRange(new TabPage[] { tabERD, tabOptionSets, tabRoles, tabFlows, tabDocumentation });
            tabLeft.SelectedIndexChanged += tabLeft_SelectedIndexChanged;

            // ── Status label ───────────────────────────────────────────────
            lblStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(71, 85, 105),
                Text = "Not connected.",
                Padding = new Padding(4, 0, 0, 0),
                BorderStyle = BorderStyle.Fixed3D
            };

            // ── Left panel assembly ────────────────────────────────────────
            var leftPanel = new Panel { Dock = DockStyle.Fill };
            leftPanel.Controls.Add(tabLeft);
            leftPanel.Controls.Add(pnlSolution);
            leftPanel.Controls.Add(lblStatus);

            // ── WebView2 ───────────────────────────────────────────────────
            webView = new WebView2 { Dock = DockStyle.Fill };

            // ── SplitContainer ─────────────────────────────────────────────
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterDistance = 270,
                Panel1MinSize = 220
            };
            splitMain.Panel1.Controls.Add(leftPanel);
            splitMain.Panel2.Controls.Add(webView);

            // ── Plugin control ─────────────────────────────────────────────
            Controls.Add(splitMain);
            Controls.Add(toolStrip);
            Name = "DataversePluginControl";
            Size = new Size(1100, 640);
            Load += DataversePluginControl_Load;

            // Resize entity list and generate button to fill ERD tab height
            tabERD.Resize += (s, ev) =>
            {
                int h = tabERD.ClientSize.Height;
                lstEntities.Height = Math.Max(60, h - 74);
                btnGenerate.Top = lstEntities.Bottom + 6;
            };
        }
    }
}
