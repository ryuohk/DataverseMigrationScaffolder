using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using DataverseMigrationScaffolder.Core;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using Label = System.Windows.Forms.Label;                       // Microsoft.Xrm.Sdk also defines Label
using SolutionInfo = DataverseMigrationScaffolder.Core.SolutionInfo; // Microsoft.Xrm.Sdk also defines SolutionInfo

namespace DataverseMigrationScaffolder
{
    public partial class MainControl : PluginControlBase, IGitHubPlugin, IHelpPlugin
    {
        // Tool Library / in-app links
        public string RepositoryName { get { return "DataverseMigrationScaffolder"; } }
        public string UserName { get { return "ryuohk"; } }
        public string HelpUrl { get { return "https://github.com/ryuohk/DataverseMigrationScaffolder#readme"; } }

        private ToolSettings _settings = new ToolSettings();
        private List<EntityMetadata> _allTables = new List<EntityMetadata>();
        private GenerationResult _lastResult;

        /// <summary>Attribute metadata cache for the current session/connection.</summary>
        private readonly Dictionary<string, EntityMetadata> _metadataCache =
            new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Checked state for ALL tables (visible or filtered out), per environment.</summary>
        private readonly HashSet<string> _checkedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _selectionOrgKey;   // org whose selection _checkedTables currently holds

        // Solution filtering
        private List<SolutionInfo> _solutions = new List<SolutionInfo>();
        private SolutionFilter _solutionFilter;    // null = Default solution (no filtering)
        private bool _suppressSolutionEvent;

        // UI
        private ToolTip _tip;
        private ComboBox _cboSolution;
        private Label _lblOutputFolder;
        private TextBox _txtFilter;
        private ComboBox _cboCategory;
        private CheckBox _chkCheckedOnly;
        private TextBox _txtSchema;
        private NumericUpDown _numBatch;
        private Button _btnGenerate;
        private TextBox _txtStagingPrefix;
        private TextBox _txtGuidPrefix;
        private TextBox _txtMatchKey;
        private ComboBox _cboStagingMode;
        private ComboBox _cboGuidMode;
        private DataGridView _grid;
        private ListBox _lstFiles;
        private TextBox _txtPreview;
        private TextBox _txtWarnings;
        private ToolStripStatusLabel _sslOrg;
        private ToolStripStatusLabel _sslChecked;
        private ToolStripStatusLabel _sslOutput;
        private ToolStripStatusLabel _sslLast;

        public MainControl()
        {
            InitializeComponent();
        }

        private string CurrentOrgKey
        {
            get
            {
                if (ConnectionDetail != null && !string.IsNullOrEmpty(ConnectionDetail.Organization))
                {
                    return ConnectionDetail.Organization;
                }
                return "default";
            }
        }

        #region UI construction

        private void InitializeComponent()
        {
            Name = "MainControl";
            Size = new Size(1320, 720);

            _tip = Tips.New();

            // ---- Band 1: steps 1 and 2 ----------------------------------------------
            var pnlSteps12 = new Panel { Dock = DockStyle.Top, Height = 74 };

            var grpStep1 = new GroupBox
            {
                Text = "Step 1  -  Load tables from the connected environment",
                Location = new Point(8, 2),
                Size = new Size(566, 66)
            };

            var btnLoadTables = NewStepButton("Load Tables", new Point(12, 24), new Size(122, 30));
            btnLoadTables.Click += (s, e) => ExecuteMethod(LoadTables);

            var lblSolution = new Label { Text = "Solution:", Location = new Point(146, 32), AutoSize = true };
            _cboSolution = new ComboBox
            {
                Location = new Point(202, 28),
                Width = 226,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboSolution.SelectedIndexChanged += (s, e) => OnSolutionSelectionChanged();

            var btnExclusions = new Button
            {
                Text = "Dependency Exclusions...",
                Location = new Point(436, 27),
                Size = new Size(118, 26),
                FlatStyle = FlatStyle.System
            };
            btnExclusions.Click += (s, e) => EditDependencyExclusions();

            grpStep1.Controls.AddRange(new Control[] { btnLoadTables, lblSolution, _cboSolution, btnExclusions });

            var grpStep2 = new GroupBox
            {
                Text = "Step 2  -  Set the output folder",
                Location = new Point(582, 2),
                Size = new Size(716, 66),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var btnOutputFolder = NewStepButton("Set Output Folder", new Point(12, 24), new Size(146, 30));
            btnOutputFolder.Click += (s, e) => PickOutputFolder();

            _lblOutputFolder = new Label
            {
                Text = "(no output folder - preview only)",
                Location = new Point(168, 32),
                AutoSize = true,
                ForeColor = Color.DimGray
            };

            grpStep2.Controls.AddRange(new Control[] { btnOutputFolder, _lblOutputFolder });

            pnlSteps12.Controls.Add(grpStep1);
            pnlSteps12.Controls.Add(grpStep2);

            // ---- Band 2: step 3 (choices) and step 4 (generate) ----------------------
            var pnlSteps34 = new Panel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(8, 2, 8, 6) };

            var pnlGenerate = new Panel { Dock = DockStyle.Right, Width = 220, Padding = new Padding(14, 18, 0, 4) };

            _btnGenerate = new Button
            {
                Text = "Generate SSIS Project",
                Font = new Font(Font, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Fill
            };
            _btnGenerate.FlatAppearance.BorderSize = 0;
            _btnGenerate.Click += (s, e) => ExecuteMethod(GenerateSsisProject);

            var lblStep4 = new Label
            {
                Text = "Step 4",
                Dock = DockStyle.Top,
                Height = 16,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 84, 153)
            };

            pnlGenerate.Controls.Add(_btnGenerate);
            var secondary = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 32, ColumnCount = 2, Padding = new Padding(0, 4, 0, 0) };
            secondary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            secondary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var btnExport = new Button { Text = "Export  \u25BE", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 2, 0) };
            var exportMenu = new ContextMenuStrip { ShowItemToolTips = true };
            exportMenu.Items.Add("SQL scripts (staging and GUID tables)", null, (s, e) => ExecuteMethod(() => Export(ExportKind.SqlScripts)))
                .ToolTipText = Tips.Wrap("Saves the CREATE TABLE scripts for the checked tables: NN_create_staging.sql and "
                    + "NN_create_guid.sql, one file per dependency tier (01 first). Run them in number order to build the "
                    + "staging and GUID tables yourself, for example on a new SQL Server.");
            exportMenu.Items.Add("Data dictionary (Excel)", null, (s, e) => ExecuteMethod(() => Export(ExportKind.DataDictionary)))
                .ToolTipText = Tips.Wrap("Saves data_dictionary.xlsx: one sheet per checked table (ordered by display name) listing "
                    + "every column's logical name, display name, type, lookup targets, description and SQL type, plus a "
                    + "~Tables sheet with each table's tier and file. Useful for mapping legacy columns with the business.");
            exportMenu.Items.Add("Scaffolder run (manifest, scripts, metadata seed)", null, (s, e) => ExecuteMethod(() => Export(ExportKind.ScaffolderRun)))
                .ToolTipText = Tips.Wrap("Saves everything an SSIS project is built from: the SQL scripts, manifest.json (tables, "
                    + "tiers, columns and lookups) and meta_seed.sql (harness metadata). Use it to build or rebuild a project "
                    + "later without connecting to Dataverse: SSIS Settings... > A previous scaffolder run.");
            btnExport.Click += (s, e) => exportMenu.Show(btnExport, new Point(0, btnExport.Height));
            var btnSsisSettings = new Button { Text = "SSIS Settings...", Dock = DockStyle.Fill, Margin = new Padding(2, 0, 0, 0) };
            btnSsisSettings.Click += (s, e) => OpenSsisSettings();
            secondary.Controls.Add(btnExport, 0, 0);
            secondary.Controls.Add(btnSsisSettings, 1, 0);
            pnlGenerate.Controls.Add(secondary);
            pnlGenerate.Controls.Add(lblStep4);
            _btnGenerate.BringToFront();

            var grpStep3 = new GroupBox
            {
                Text = "Step 3  -  Choose the tables and how they are built",
                Dock = DockStyle.Fill
            };

            // Row 1: which tables are listed
            var lblList = NewRowLabel("Table list:", 27);
            var lblFilter = new Label { Text = "Filter:", Location = new Point(96, 27), AutoSize = true };
            _txtFilter = new TextBox { Location = new Point(138, 24), Width = 180 };

            var lblCategory = new Label { Text = "Category:", Location = new Point(332, 27), AutoSize = true };
            _cboCategory = new ComboBox
            {
                Location = new Point(394, 23),
                Width = 90,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboCategory.Items.Add("All");   // real prefixes are added after Load Tables
            _cboCategory.SelectedIndex = 0;

            _chkCheckedOnly = new CheckBox { Text = "Checked only", Location = new Point(498, 26), AutoSize = true };

            // Row 2: staging tables, and where all tables go
            var lblStaging = NewRowLabel("Staging tables:", 56);
            var lblStagingPrefix = new Label { Text = "Prefix:", Location = new Point(96, 56), AutoSize = true };
            _txtStagingPrefix = new TextBox { Location = new Point(138, 52), Width = 62 };
            _cboStagingMode = NewModeCombo(new Point(206, 52));

            var lblSchema = new Label { Text = "Schema:", Location = new Point(362, 56), AutoSize = true };
            _txtSchema = new TextBox { Location = new Point(418, 52), Width = 55 };

            var lblBatch = new Label { Text = "Tables per file:", Location = new Point(490, 56), AutoSize = true };
            _numBatch = new NumericUpDown
            {
                Location = new Point(578, 52),
                Width = 55,
                Minimum = 1,
                Maximum = 500,
                Value = 40
            };

            // Row 3: GUID tables and the legacy match key they record
            var lblGuid = NewRowLabel("GUID tables:", 85);
            var lblGuidPrefix = new Label { Text = "Prefix:", Location = new Point(96, 85), AutoSize = true };
            _txtGuidPrefix = new TextBox { Location = new Point(138, 82), Width = 62 };
            _cboGuidMode = NewModeCombo(new Point(206, 82));

            var lblMatchKey = new Label { Text = "Match key:", Location = new Point(362, 85), AutoSize = true };
            _txtMatchKey = new TextBox { Location = new Point(430, 82), Width = 84 };

            grpStep3.Controls.AddRange(new Control[]
            {
                lblList, lblFilter, _txtFilter, lblCategory, _cboCategory, _chkCheckedOnly,
                lblStaging, lblStagingPrefix, _txtStagingPrefix, _cboStagingMode, lblSchema, _txtSchema, lblBatch, _numBatch,
                lblGuid, lblGuidPrefix, _txtGuidPrefix, _cboGuidMode, lblMatchKey, _txtMatchKey
            });

            pnlSteps34.Controls.Add(grpStep3);
            pnlSteps34.Controls.Add(pnlGenerate);
            grpStep3.BringToFront();   // index 0 docks last, so Fill claims what Right leaves

            // ---- Tooltips -------------------------------------------------------------
            // Step 1
            Tips.Set(_tip, "Connects to the environment chosen in XrmToolBox and lists its tables and solutions. Start here.\n"
                + "Click it again to refresh the list and clear cached metadata, so changes made in Dataverse since then are picked up.",
                btnLoadTables);
            Tips.Set(_tip, "Limits the tables and columns to one solution's components. Default = every table and column in the environment.\n"
                + "A table added to the solution with all its subcomponents keeps all its columns; otherwise only the columns added to "
                + "the solution are used. The primary id and primary name are always kept. Remembered per environment.",
                lblSolution, _cboSolution);
            Tips.Set(_tip, "Lookup fields to ignore when ordering tables by dependency, e.g. ownerid, createdby, modifiedby.\n"
                + "Tables are loaded in tiers so that the record a lookup points to always exists first. System lookups such as owner "
                + "point at tables you usually don't migrate, so excluding them keeps the tiers sensible. The columns are still generated.",
                btnExclusions);

            // Step 2
            Tips.Set(_tip, "Choose the folder where SSIS projects and exported files are saved.\n"
                + "Each Generate SSIS Project creates a new SSIS-<date-time> folder inside it. Without an output folder, Export only "
                + "shows the files in the preview.",
                btnOutputFolder);

            // Step 3: table list
            Tips.Set(_tip, "These only change which tables the grid shows. Every checked table is generated, including checked tables "
                + "hidden by a filter.",
                lblList);
            Tips.Set(_tip, "Show only tables whose logical or display name contains this text.", lblFilter, _txtFilter);
            Tips.Set(_tip, "Show only tables with this publisher prefix (the part of the logical name before the underscore, e.g. contoso). "
                + "\"oob\" = out-of-the-box tables with no prefix.",
                lblCategory, _cboCategory);
            Tips.Set(_tip, "Show only the tables that are checked, to review the selection.", _chkCheckedOnly);

            // Step 3: staging tables
            Tips.Set(_tip, "Staging tables hold the legacy data on its way into Dataverse: the Stage tasks copy it from the Legacy database, "
                + "and the Migrate data flows load it from there. There is one staging table per checked table, with a column for every "
                + "Dataverse field; its legacy ID column is UNIQUE.",
                lblStaging);
            Tips.Set(_tip, "Put in front of each staging table's name, e.g. stage_ gives stage_contoso_Project.", lblStagingPrefix, _txtStagingPrefix);
            Tips.Set(_tip, "What Create Staging Tables does with tables that already exist:\n"
                + "- Drop & recreate (default): deletes and rebuilds them on every run, so they always match the current Dataverse "
                + "columns. Any data in them is lost.\n"
                + "- Create if missing: creates only tables that don't exist yet. Existing tables and their data are left alone, even "
                + "if the Dataverse columns have changed.",
                _cboStagingMode);
            Tips.Set(_tip, "SQL Server schema for all staging and GUID tables (default dbo).", lblSchema, _txtSchema);
            Tips.Set(_tip, "The most tables per SQL file and per SSIS package pair (NNa - Staging and NNb - Harness).\n"
                + "Tables are grouped by dependency tier, and tiers are never mixed: a tier with more tables than this is split into "
                + "parts, and a smaller tier gets its own shorter file.",
                lblBatch, _numBatch);

            // Step 3: GUID tables
            Tips.Set(_tip, "GUID tables hold two columns for every record created in Dataverse: its new record ID and its legacy ID, which "
                + "is UNIQUE, so each legacy record maps to one Dataverse record. "
                + "Tables loaded later use them to turn the legacy IDs in lookup columns into Dataverse record IDs, and the Deferred "
                + "Updates package uses them to fill in lookups that had to wait.",
                lblGuid);
            Tips.Set(_tip, "Put in front of each GUID table's name, e.g. guid_ gives guid_contoso_Project.", lblGuidPrefix, _txtGuidPrefix);
            Tips.Set(_tip, "What Create GUID Tables does with tables that already exist:\n"
                + "- Create if missing (default): keeps them, with the ID mappings recorded by earlier runs.\n"
                + "- Drop & recreate: deletes them on every run. Only use this when Dataverse is emptied too; otherwise lookups to "
                + "records loaded by earlier runs can no longer be resolved.",
                _cboGuidMode);
            Tips.Set(_tip, "How the legacy ID column is recognised: a column whose name ends with one of these suffixes (comma-separated) "
                + "is the match key. The default, legacyid, matches new_legacyid, contoso_legacyid and so on.\n"
                + "Tables without a match-key column are left out of the SSIS project.",
                lblMatchKey, _txtMatchKey);

            // Step 4
            Tips.Set(_tip, "Builds the complete SSIS migration project for the checked tables.\n"
                + "It retrieves the latest metadata for each checked table, orders the tables by dependency, and writes a new "
                + "SSIS-<date-time> folder in the output folder. That folder holds the project, its SQL query files, and a Scaffolder "
                + "subfolder with the scripts, manifest and metadata seed it was built from.\n"
                + "The first time, SSIS Settings opens so you can choose the reference project to copy from.",
                lblStep4, _btnGenerate);
            Tips.Set(_tip, "Saves files for the checked tables to the output folder without building an SSIS project:\n"
                + "- SQL scripts: CREATE TABLE scripts for the staging and GUID tables\n"
                + "- Data dictionary: an Excel workbook describing every table and column\n"
                + "- Scaffolder run: scripts, manifest and metadata seed, to build a project later without connecting",
                btnExport);
            Tips.Set(_tip, "Choose the reference SSIS project or solution to copy from, the template package with the sample data flow, "
                + "and the new project's name. These are remembered for Generate SSIS Project.\n"
                + "You can also build a project from a previous scaffolder run here, without connecting to Dataverse.",
                btnSsisSettings);

            // ---- Grid ---------------------------------------------------------------
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            _grid.Columns.Add(NewCheckColumn("colInclude", "Include", 75));
            _grid.Columns.Add(NewTextColumn("colLogical", "Logical Name"));
            _grid.Columns.Add(NewTextColumn("colDisplay", "Display Name"));
            _grid.Columns.Add(NewTextColumn("colCategory", "Category"));

            WireHeaderCheckBox("colInclude", "Include");
            _grid.Columns["colInclude"].ToolTipText = Tips.Wrap("Check the tables to migrate. The header checkbox checks or unchecks "
                + "every table the grid currently shows. Checked tables are remembered per environment.");
            _grid.Columns["colLogical"].ToolTipText = "The table's logical (schema) name in Dataverse";
            _grid.Columns["colDisplay"].ToolTipText = "The table's display name, as users see it in Dataverse";
            _grid.Columns["colCategory"].ToolTipText = Tips.Wrap("Publisher prefix from the logical name; \"oob\" = out-of-the-box table "
                + "with no prefix");

            // Commit checkbox clicks immediately so the stored state is always current.
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                if (_grid.Columns[e.ColumnIndex].Name != "colInclude") return;
                var row = _grid.Rows[e.RowIndex];
                var logical = Convert.ToString(row.Cells["colLogical"].Value);
                if (string.IsNullOrEmpty(logical)) return;
                if (IsChecked(row, "colInclude")) _checkedTables.Add(logical);
                else _checkedTables.Remove(logical);
                if (!_bulkUpdating)
                {
                    UpdateHeaderCheckState();
                    UpdateCheckedCount();
                }
            };

            // ---- Right side: files + preview + warnings ----------------------------
            _lstFiles = new ListBox { Dock = DockStyle.Fill };
            _lstFiles.SelectedIndexChanged += (s, e) => ShowSelectedFile();
            Tips.Set(_tip, "The results of the last run; select one to preview it below. After Generate SSIS Project, the first entry is "
                + "the project summary: packages, tiers, skipped tables and anything that needs attention.",
                _lstFiles);

            _txtPreview = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                Text = QuickStartText()
            };
            Tips.Set(_tip, "The contents of the file selected above (read-only). Before the first run, this shows the quick-start guide.", _txtPreview);

            _txtWarnings = new TextBox
            {
                Dock = DockStyle.Bottom,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Height = 80,
                ForeColor = Color.DimGray,
                Text = "Warnings appear here after generation - e.g. dependency cycles that were broken " +
                       "(those lookups need a deferred UPDATE pass after the initial load)."
            };
            Tips.Set(_tip, "Problems found by the last run, e.g. circular lookups that had to be broken (table A looks up B and B looks up "
                + "A). Those lookups are left empty on the first load and filled in afterwards by the Deferred Updates package.",
                _txtWarnings);

            var rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            var lblFiles = new Label { Dock = DockStyle.Top, Height = 18, Text = "Generated files (select to preview):" };
            _tip.SetToolTip(lblFiles, _tip.GetToolTip(_lstFiles));
            rightSplit.Panel1.Controls.Add(_lstFiles);
            rightSplit.Panel1.Controls.Add(lblFiles);
            rightSplit.Panel2.Controls.Add(_txtPreview);
            rightSplit.Panel2.Controls.Add(_txtWarnings);

            var mainSplit = new SplitContainer { Dock = DockStyle.Fill };
            mainSplit.Panel1.Controls.Add(_grid);
            mainSplit.Panel2.Controls.Add(rightSplit);

            // ---- Status bar ----------------------------------------------------------
            var status = new StatusStrip { ShowItemToolTips = true };
            _sslOrg = new ToolStripStatusLabel("(not connected)");
            _sslChecked = new ToolStripStatusLabel("Checked: 0");
            _sslLast = new ToolStripStatusLabel("");
            _sslOutput = new ToolStripStatusLabel("(no output folder - preview only)") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
            _sslOrg.ToolTipText = "The connected Dataverse environment";
            _sslChecked.ToolTipText = "Tables checked for generation, including checked tables hidden by a filter";
            _sslLast.ToolTipText = "What the most recent run produced";
            _sslOutput.ToolTipText = "The output folder";
            status.Items.AddRange(new ToolStripItem[] { _sslOrg, new ToolStripStatusLabel("|"), _sslChecked, new ToolStripStatusLabel("|"), _sslLast, _sslOutput });

            // Docking resolves in reverse index order, so the Fill control goes in first and
            // the Top bands follow in reverse visual order (step 1 band added last = topmost).
            Controls.Add(mainSplit);
            Controls.Add(pnlSteps34);
            Controls.Add(pnlSteps12);
            Controls.Add(status);

            _txtFilter.TextChanged += (s, e) => ApplyFilter();
            _cboCategory.SelectedIndexChanged += (s, e) => ApplyFilter();
            _chkCheckedOnly.CheckedChanged += (s, e) => ApplyFilter();

            Load += (s, e) =>
            {
                LoadSettings();
                UpdateOrgLabel();
                // SplitterDistance can only be set safely once the control has its real size.
                try
                {
                    mainSplit.SplitterDistance = Math.Max(200, Width / 2);
                    rightSplit.SplitterDistance = 140;
                }
                catch (InvalidOperationException) { /* tiny host window; keep defaults */ }
            };
        }

        private static string QuickStartText()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "DATAVERSE MIGRATION SCAFFOLDER - QUICK START",
                "",
                "  Connect to an environment first (top-left of XrmToolBox), then work",
                "  through the four numbered steps above.",
                "",
                "  STEP 1  Load Tables",
                "     Retrieves tables and solutions from the environment. Then pick a",
                "     Solution to scope both tables AND columns to its components",
                "     (Default = everything; the choice is remembered per environment).",
                "",
                "  STEP 2  Set Output Folder",
                "     Where the generated files are written. Without one, generation",
                "     runs preview-only and nothing reaches disk.",
                "",
                "  STEP 3  Filter and choose outputs",
                "     Check the tables to include in the grid on the left. The header",
                "     checkbox toggles every row shown by the current filter, and",
                "     selections are remembered per environment.",
                "",
                "     Staging and GUID tables: name prefixes, drop-vs-create handling,",
                "     schema, tables per file and the legacy match key.",
                "",
                "  STEP 4  Generate SSIS Project builds the migration harness straight from",
                "          the checked tables and your reference SSIS project (chosen once",
                "          under SSIS Settings...), in a new SSIS-<date> folder.",
                "          Export saves SQL scripts, a data dictionary or a scaffolder",
                "          run (to rebuild a project later without connecting).",
                "",
                "  Files are batched strictly by dependency tier: everything in a file",
                "  depends only on tables from the same or earlier files - matching",
                "  SSIS/ETL packages organized by load order.",
                "",
                "  Hover any control for details. Full documentation: Help menu or the",
                "  project website (GitHub)."
            });
        }

        private static Label NewRowLabel(string text, int y)
        {
            return new Label { Text = text, Location = new Point(10, y), AutoSize = true, ForeColor = Color.DimGray };
        }

        /// <summary>
        /// Outlined accent button for the two prerequisite steps, so they read as the way in
        /// without competing with the solid Generate button that ends the flow.
        /// </summary>
        private Button NewStepButton(string text, Point location, Size size)
        {
            var button = new Button
            {
                Text = text,
                Location = location,
                Size = size,
                Font = new Font(Font, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(0, 84, 153),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 215);
            button.FlatAppearance.BorderSize = 1;
            return button;
        }

        private static ComboBox NewModeCombo(Point location)
        {
            var combo = new ComboBox
            {
                Location = location,
                Width = 132,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            // ComboBox items render '&' literally (no mnemonic processing), unlike button text.
            combo.Items.AddRange(new object[] { "Drop & recreate", "Create if missing" });
            combo.SelectedIndex = 0;
            return combo;
        }

        private void WireHeaderCheckBox(string columnName, string headerText)
        {
            var headerCell = new CheckBoxHeaderCell
            {
                ToolTipText = "Check/uncheck all rows currently shown by the filter"
            };
            headerCell.Style.Padding = new Padding(18, 0, 0, 0);
            headerCell.CheckedChanged += (s, isChecked) => SetAllVisible(columnName, isChecked);

            var column = _grid.Columns[columnName];
            column.HeaderCell = headerCell;
            column.HeaderText = headerText;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        }

        private bool _bulkUpdating;

        /// <summary>Applies a check state to every row currently visible (filtered) in the grid.</summary>
        private void SetAllVisible(string columnName, bool value)
        {
            ExitEditMode();
            _bulkUpdating = true;
            _grid.SuspendLayout();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                row.Cells[columnName].Value = value;   // CellValueChanged keeps _checkedTables in sync
            }
            _grid.ResumeLayout();
            _bulkUpdating = false;
            _grid.Invalidate();
            UpdateHeaderCheckState();
            UpdateCheckedCount();
        }

        /// <summary>
        /// Header checkbox mirrors the visible rows: checked only when every row currently
        /// shown by the filter is checked (an empty list shows unchecked).
        /// </summary>
        private void UpdateHeaderCheckState()
        {
            var header = _grid.Columns["colInclude"].HeaderCell as CheckBoxHeaderCell;
            if (header == null) return;

            var allChecked = _grid.Rows.Count > 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!IsChecked(row, "colInclude")) { allChecked = false; break; }
            }
            header.SetState(allChecked);
        }

        private void UpdateCheckedCount()
        {
            _sslChecked.Text = string.Format("Checked: {0}", _checkedTables.Count);
        }

        private void UpdateOrgLabel()
        {
            _sslOrg.Text = ConnectionDetail != null
                ? "Org: " + (ConnectionDetail.ConnectionName ?? CurrentOrgKey)
                : "(not connected)";
        }

        /// <summary>
        /// Drops any in-place checkbox editor. While a cell is in edit mode it paints the
        /// editor's value, not the cell value, so programmatic changes look like they
        /// didn't happen until the selection moves.
        /// </summary>
        private void ExitEditMode()
        {
            _grid.EndEdit();
            _grid.CurrentCell = null;
        }

        private static DataGridViewCheckBoxColumn NewCheckColumn(string name, string header, int width)
        {
            return new DataGridViewCheckBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            };
        }

        private static DataGridViewTextBoxColumn NewTextColumn(string name, string header)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                ReadOnly = true
            };
        }

        #endregion

        #region Connection switching

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);

            var newKey = detail != null && !string.IsNullOrEmpty(detail.Organization) ? detail.Organization : "default";
            if (!string.Equals(_selectionOrgKey, newKey, StringComparison.OrdinalIgnoreCase))
            {
                StoreSelection();                       // persist the outgoing org's picks
                LoadSelectionFor(newKey);               // pull in the new org's picks
                _metadataCache.Clear();                 // metadata is environment-specific
                _allTables.Clear();
                _solutions.Clear();
                _solutionFilter = null;
                if (_cboSolution != null)
                {
                    _suppressSolutionEvent = true;
                    _cboSolution.Items.Clear();
                    _suppressSolutionEvent = false;
                }
                if (_grid != null) { _grid.Rows.Clear(); UpdateCheckedCount(); }
            }
            UpdateOrgLabel();
        }

        private void LoadSelectionFor(string orgKey)
        {
            _checkedTables.Clear();
            foreach (var t in _settings.GetSelection(orgKey)) _checkedTables.Add(t);
            _selectionOrgKey = orgKey;
        }

        private void StoreSelection()
        {
            if (_selectionOrgKey != null)
            {
                _settings.SetSelection(_selectionOrgKey,
                    _checkedTables.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList());
            }
        }

        #endregion

        #region Settings

        private void LoadSettings()
        {
            ToolSettings loaded;
            if (SettingsManager.Instance.TryLoad(GetType(), out loaded) && loaded != null)
            {
                _settings = loaded;
            }
            _txtSchema.Text = _settings.SchemaName;
            var batch = Math.Max((int)_numBatch.Minimum, Math.Min((int)_numBatch.Maximum, _settings.BatchSize));
            _numBatch.Value = batch;

            LoadSelectionFor(CurrentOrgKey);
            UpdateCheckedCount();

            _txtStagingPrefix.Text = _settings.StagingPrefix ?? "stage_";
            _txtGuidPrefix.Text = _settings.GuidPrefix ?? "guid_";
            _txtMatchKey.Text = _settings.MatchKeySuffixes ?? "legacyid";
            _cboStagingMode.SelectedIndex = _settings.StagingDropRecreate ? 0 : 1;
            _cboGuidMode.SelectedIndex = _settings.GuidDropRecreate ? 0 : 1;

            UpdateOutputFolderLabel();
        }

        private void SaveSettings()
        {
            CaptureSettingsFromUi();
            SettingsManager.Instance.Save(GetType(), _settings);
        }

        private void CaptureSettingsFromUi()
        {
            _settings.SchemaName = string.IsNullOrWhiteSpace(_txtSchema.Text) ? "dbo" : _txtSchema.Text.Trim();
            _settings.BatchSize = (int)_numBatch.Value;
            StoreSelection();
            _settings.StagingPrefix = _txtStagingPrefix.Text == null ? "" : _txtStagingPrefix.Text.Trim();
            _settings.GuidPrefix = _txtGuidPrefix.Text == null ? "" : _txtGuidPrefix.Text.Trim();
            _settings.MatchKeySuffixes = _txtMatchKey.Text == null ? "" : _txtMatchKey.Text.Trim();
            _settings.StagingDropRecreate = _cboStagingMode.SelectedIndex == 0;
            _settings.GuidDropRecreate = _cboGuidMode.SelectedIndex == 0;
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            SaveSettings();
            base.ClosingPlugin(info);
        }

        private void UpdateOutputFolderLabel()
        {
            var hasFolder = !string.IsNullOrEmpty(_settings.OutputFolder);
            var text = hasFolder ? _settings.OutputFolder : "(no output folder - preview only)";

            _sslOutput.Text = text;
            _lblOutputFolder.Text = hasFolder ? Shorten(text, 70) : text;
            _lblOutputFolder.ForeColor = hasFolder ? Color.Black : Color.DimGray;
            if (_tip != null) _tip.SetToolTip(_lblOutputFolder, text);
        }

        private static string Shorten(string path, int max)
        {
            if (path.Length <= max) return path;
            return path.Substring(0, 18) + "..." + path.Substring(path.Length - (max - 21));
        }

        private void PickOutputFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Folder where the generated .sql files will be written";
                if (!string.IsNullOrEmpty(_settings.OutputFolder)) dialog.SelectedPath = _settings.OutputFolder;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _settings.OutputFolder = dialog.SelectedPath;
                    UpdateOutputFolderLabel();
                }
            }
        }

        private void EditDependencyExclusions()
        {
            using (var dialog = new ExclusionsDialog(_settings.DependencyExclusions))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _settings.DependencyExclusions = dialog.Result;
                }
            }
        }

        #endregion

        #region Load tables

        private void LoadTables()
        {
            _metadataCache.Clear();   // full refresh requested

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Retrieving tables and solutions...",
                Work = (worker, args) =>
                {
                    var service = new MetadataService(Service);
                    var tables = service.GetAllTables();
                    var solutions = service.GetSolutions();
                    args.Result = Tuple.Create(tables, solutions);
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(this, args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    var result = (Tuple<List<EntityMetadata>, List<SolutionInfo>>)args.Result;
                    _allTables = result.Item1;
                    _solutions = result.Item2;
                    PopulateSolutionPicker();
                    PopulateCategoryFilter();
                    UpdateCheckedCount();
                    ApplySolutionSelection();   // ends with ApplyFilter()
                }
            });
        }

        /// <summary>Fills the solution dropdown, restoring the remembered choice (default: Default).</summary>
        private void PopulateSolutionPicker()
        {
            _suppressSolutionEvent = true;
            _cboSolution.Items.Clear();
            foreach (var solution in _solutions) _cboSolution.Items.Add(solution);

            // The dropdown defaults to the control's width and truncates long names -
            // widen it to fit the longest entry.
            var dropDownWidth = _cboSolution.Width;
            foreach (var solution in _solutions)
            {
                var w = TextRenderer.MeasureText(solution.ToString(), _cboSolution.Font).Width
                        + SystemInformation.VerticalScrollBarWidth;
                if (w > dropDownWidth) dropDownWidth = w;
            }
            _cboSolution.DropDownWidth = dropDownWidth;

            var remembered = _settings.GetSolution(CurrentOrgKey);
            var index = 0;
            for (var i = 0; i < _solutions.Count; i++)
            {
                if (string.Equals(_solutions[i].UniqueName, remembered, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            if (_cboSolution.Items.Count > 0) _cboSolution.SelectedIndex = index;
            _suppressSolutionEvent = false;
        }

        private void OnSolutionSelectionChanged()
        {
            if (_suppressSolutionEvent) return;
            var solution = _cboSolution.SelectedItem as SolutionInfo;
            _settings.SetSolution(CurrentOrgKey, solution != null ? solution.UniqueName : "Default");
            ExecuteMethod(ApplySolutionSelection);
        }

        /// <summary>Loads solution components (unless Default) and re-filters the grid.</summary>
        private void ApplySolutionSelection()
        {
            var solution = _cboSolution.SelectedItem as SolutionInfo;

            if (solution == null || string.Equals(solution.UniqueName, "Default", StringComparison.OrdinalIgnoreCase))
            {
                _solutionFilter = null;
                ApplyFilter();
                return;
            }

            WorkAsync(new WorkAsyncInfo
            {
                Message = string.Format("Loading components of solution '{0}'...", solution.FriendlyName),
                Work = (worker, args) =>
                {
                    var service = new MetadataService(Service);
                    args.Result = service.GetSolutionFilter(solution.Id);
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(this, args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    _solutionFilter = (SolutionFilter)args.Result;
                    ApplyFilter();
                }
            });
        }

        /// <summary>True when the table is visible under the current solution selection.</summary>
        private bool InCurrentSolution(EntityMetadata entity)
        {
            if (_solutionFilter == null) return true;
            return entity.MetadataId.HasValue && _solutionFilter.Entities.ContainsKey(entity.MetadataId.Value);
        }

        /// <summary>Rebuilds the Category dropdown from the prefixes actually present.</summary>
        private void PopulateCategoryFilter()
        {
            var previous = _cboCategory.SelectedItem == null ? "All" : _cboCategory.SelectedItem.ToString();

            var prefixes = _allTables
                .Select(t => MetadataService.GetPrefix(t.LogicalName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _cboCategory.Items.Clear();
            _cboCategory.Items.Add("All");
            foreach (var p in prefixes) _cboCategory.Items.Add(p);

            var restored = _cboCategory.Items.IndexOf(previous);
            _cboCategory.SelectedIndex = restored >= 0 ? restored : 0;
        }

        private void ApplyFilter()
        {
            var filter = _txtFilter.Text == null ? "" : _txtFilter.Text.Trim();
            var categoryFilter = _cboCategory.SelectedItem == null ? "All" : _cboCategory.SelectedItem.ToString();
            var checkedOnly = _chkCheckedOnly.Checked;

            ExitEditMode();
            _grid.SuspendLayout();
            _grid.Rows.Clear();

            foreach (var entity in _allTables)
            {
                var logical = entity.LogicalName;
                var display = entity.DisplayName != null && entity.DisplayName.UserLocalizedLabel != null
                    ? entity.DisplayName.UserLocalizedLabel.Label
                    : "";
                var category = MetadataService.GetPrefix(logical);

                if (!InCurrentSolution(entity)) continue;

                if (checkedOnly && !_checkedTables.Contains(logical)) continue;

                if (categoryFilter != "All" && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase)) continue;

                if (filter.Length > 0 &&
                    logical.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    display.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var index = _grid.Rows.Add();
                var row = _grid.Rows[index];
                row.Cells["colLogical"].Value = logical;
                row.Cells["colDisplay"].Value = display;
                row.Cells["colCategory"].Value = category;
                row.Cells["colInclude"].Value = _checkedTables.Contains(logical);
            }

            _grid.ResumeLayout();
            UpdateHeaderCheckState();
        }

        private static bool IsChecked(DataGridViewRow row, string column)
        {
            var value = row.Cells[column].Value;
            return value is bool && (bool)value;
        }

        #endregion

        #region Generate

        /// <summary>Checked tables that exist in the connected environment and the selected solution.</summary>
        private List<string> CheckedPicks()
        {
            var known = new HashSet<string>(
                _allTables.Where(InCurrentSolution).Select(t => t.LogicalName),
                StringComparer.OrdinalIgnoreCase);
            return _checkedTables.Where(t => known.Contains(t))
                                 .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                                 .ToList();
        }

        /// <summary>Export: save one kind of output to the output folder, without an SSIS project.</summary>
        private void Export(ExportKind kind)
        {
            CaptureSettingsFromUi();
            ExitEditMode();
            var picks = CheckedPicks();
            if (picks.Count == 0)
            {
                MessageBox.Show(this, "Load tables and check at least one first.",
                    "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RunGeneration(picks, _settings.ForExport(kind), null);
        }

        /// <summary>Generate SSIS Project: build the project from the checked tables in memory, using the
        /// remembered SSIS settings (asked for once), and save the ticked outputs too.</summary>
        private void GenerateSsisProject()
        {
            CaptureSettingsFromUi();
            ExitEditMode();
            var picks = CheckedPicks();
            if (picks.Count == 0)
            {
                MessageBox.Show(this, "Load tables and check at least one first.",
                    "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrEmpty(_settings.OutputFolder))
            {
                MessageBox.Show(this, "Set the output folder (Step 2) first: the SSIS project is written to a new folder inside it.",
                    "No output folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string output;
            if (HarnessGenerator.SettingsComplete(_settings))
            {
                output = HarnessGenerator.NewOutputFolder(_settings.OutputFolder);
            }
            else
            {
                using (var dialog = new HarnessDialog(_settings, true))
                {
                    var answer = dialog.ShowDialog(this);
                    SaveSettings();
                    if (answer != DialogResult.OK) return;
                    output = dialog.OutputFolder;
                }
            }
            RunGeneration(picks, _settings.ForHarness(), output);
        }

        /// <summary>SSIS Settings...: change the reference project, or build from a previous run's manifest.</summary>
        private void OpenSsisSettings()
        {
            CaptureSettingsFromUi();
            ExitEditMode();
            var picks = Service != null ? CheckedPicks() : new List<string>();
            using (var dialog = new HarnessDialog(_settings, picks.Count > 0))
            {
                var answer = dialog.ShowDialog(this);
                SaveSettings();
                if (answer == DialogResult.OK) ExecuteMethod(() => RunGeneration(picks, _settings.ForHarness(), dialog.OutputFolder));
            }
        }

        private sealed class RunOutcome
        {
            public GenerationResult Scripts;
            public string SsisOutput;
            public string SsisSummary;
            public string SsisError;
        }

        /// <param name="settings">which files to generate (ToolSettings.ForHarness or ForExport).</param>
        /// <param name="ssisOutput">folder for the SSIS project, or null to save the generated files only.</param>
        private void RunGeneration(List<string> picks, ToolSettings settings, string ssisOutput)
        {
            var cache = _metadataCache;
            var solutionFilter = _solutionFilter;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Retrieving attribute metadata...",
                IsCancelable = true,
                Work = (worker, args) =>
                {
                    var service = new MetadataService(Service);
                    var entities = new List<EntityMetadata>();

                    for (var i = 0; i < picks.Count; i++)
                    {
                        if (worker.CancellationPending)
                        {
                            args.Cancel = true;
                            return;
                        }

                        var pick = picks[i];
                        EntityMetadata entity;
                        if (cache.TryGetValue(pick, out entity))
                        {
                            worker.ReportProgress(i * 100 / picks.Count,
                                string.Format("{0} (cached, {1}/{2})", pick, i + 1, picks.Count));
                        }
                        else
                        {
                            worker.ReportProgress(i * 100 / picks.Count,
                                string.Format("Retrieving {0} ({1}/{2})...", pick, i + 1, picks.Count));
                            entity = service.GetTableWithAttributes(pick);
                            cache[pick] = entity;
                        }
                        entities.Add(entity);
                    }

                    worker.ReportProgress(100, "Generating scripts...");
                    var outcome = new RunOutcome { SsisOutput = ssisOutput };
                    outcome.Scripts = new ScriptGenerator(settings).Generate(
                        entities.Select(e => MetadataMapper.BuildTable(e, settings, solutionFilter)).ToList());
                    if (ssisOutput != null)
                    {
                        // The project is built from the run in memory; the run itself is kept beside it.
                        worker.ReportProgress(100, "Generating the SSIS project...");
                        try
                        {
                            outcome.SsisSummary = HarnessGenerator.GenerateFromRun(outcome.Scripts.Files, settings.HarnessProjectFile,
                                settings.HarnessPackage, ssisOutput, settings.HarnessProjectName, settings.HarnessDontSaveSensitive);
                            WriteFiles(Path.Combine(ssisOutput, HarnessGenerator.RunFolder), outcome.Scripts.Files);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException
                                                   || ex is IOException || ex is UnauthorizedAccessException)
                        {
                            outcome.SsisError = ex.Message;
                        }
                    }
                    args.Result = outcome;
                },
                ProgressChanged = e => SetWorkingMessage(e.UserState == null ? "" : e.UserState.ToString()),
                PostWorkCallBack = args =>
                {
                    if (args.Cancelled)
                    {
                        _sslLast.Text = "Last run: cancelled";
                        return;
                    }
                    if (args.Error != null)
                    {
                        MessageBox.Show(this, args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var outcome = (RunOutcome)args.Result;
                    _lastResult = outcome.Scripts;
                    ShowResult(outcome);
                    SaveSettings();   // persist the selection that produced this run
                }
            });
        }

        private static int WriteFiles(string folder, IEnumerable<GeneratedFile> files)
        {
            Directory.CreateDirectory(folder);
            var written = 0;
            foreach (var file in files)
            {
                var path = Path.Combine(folder, file.FileName);
                if (file.BinaryContent != null) File.WriteAllBytes(path, file.BinaryContent);
                else File.WriteAllText(path, file.Content, Encoding.UTF8);
                written++;
            }
            return written;
        }

        private void ShowResult(RunOutcome outcome)
        {
            var result = outcome.Scripts;
            var ssis = outcome.SsisOutput != null;
            var written = 0;
            if (!ssis && !string.IsNullOrEmpty(_settings.OutputFolder))
            {
                try { written = WriteFiles(_settings.OutputFolder, result.Files); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "The files were generated but writing them failed: " + ex.Message,
                        "Write error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            var fileCount = result.Files.Count;

            // The SSIS project summary is listed first in the preview, but never written as a file.
            if (ssis)
            {
                foreach (var file in result.Files)
                    file.Description = string.IsNullOrEmpty(file.Description) ? HarnessGenerator.RunFolder : HarnessGenerator.RunFolder + ": " + file.Description;
                result.Files.Insert(0, new GeneratedFile
                {
                    FileName = outcome.SsisError == null ? "SSIS project" : "SSIS project (failed)",
                    Description = outcome.SsisOutput,
                    Content = outcome.SsisError == null ? outcome.SsisSummary : "Generation failed: " + outcome.SsisError,
                });
            }

            _lstFiles.Items.Clear();
            foreach (var file in result.Files)
            {
                _lstFiles.Items.Add(string.IsNullOrEmpty(file.Description)
                    ? file.FileName
                    : string.Format("{0}   [{1}]", file.FileName, file.Description));
            }

            if (result.Warnings.Count == 0)
            {
                _txtWarnings.ForeColor = Color.DimGray;
                _txtWarnings.Text = "No warnings - no dependency cycles were broken.";
            }
            else
            {
                _txtWarnings.ForeColor = Color.DarkRed;
                _txtWarnings.Text = "Warnings:" + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);
            }

            if (_lstFiles.Items.Count > 0) _lstFiles.SelectedIndex = 0;

            var ok = ssis && outcome.SsisError == null;
            _sslLast.Text = ssis
                ? string.Format("Last run: {0} tables -> {1} at {2:HH:mm}", result.OrderedTables.Count, ok ? "SSIS project" : "SSIS project failed", DateTime.Now)
                : string.Format("Last run: {0} tables -> {1} files at {2:HH:mm}", result.OrderedTables.Count, fileCount, DateTime.Now);

            var summary = new StringBuilder();
            if (ssis)
            {
                if (!ok)
                {
                    summary.AppendLine("The SSIS project could not be generated:");
                    summary.AppendLine(outcome.SsisError);
                }
                else
                {
                    summary.AppendLine("SSIS project generated in " + outcome.SsisOutput);
                    var headline = (outcome.SsisSummary ?? "").Split('\n').Skip(1).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(headline)) summary.AppendLine(headline.Trim());
                    summary.AppendLine();
                    summary.AppendLine("The scaffolder run it was built from (scripts, manifest, metadata seed) is in its "
                                       + HarnessGenerator.RunFolder + " folder.");
                }
            }
            else
            {
                summary.AppendFormat("{0} tables -> {1} files.", result.OrderedTables.Count, fileCount);
                summary.AppendLine();
                summary.AppendLine(written > 0
                    ? string.Format("{0} files written to {1}", written, _settings.OutputFolder)
                    : "No output folder set - the files are shown in the preview only. Use Set Output Folder to save them.");
            }
            if (result.Warnings.Count > 0)
            {
                summary.AppendLine();
                summary.AppendFormat("{0} warning(s) - see panel below the preview.", result.Warnings.Count);
            }

            if (ok)
            {
                summary.AppendLine();
                summary.AppendLine();
                summary.Append("Open the SSIS project folder?");
                if (MessageBox.Show(this, summary.ToString(), "Generation complete", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + outcome.SsisOutput + "\"");
                return;
            }
            MessageBox.Show(this, summary.ToString(), ssis ? "SSIS project not generated" : "Export complete",
                MessageBoxButtons.OK, ssis ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void ShowSelectedFile()
        {
            if (_lastResult == null || _lstFiles.SelectedIndex < 0) return;
            _txtPreview.Text = _lastResult.Files[_lstFiles.SelectedIndex].Content;
        }

        #endregion
    }
}
