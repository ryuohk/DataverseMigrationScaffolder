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
        private CheckBox _chkStaging;
        private CheckBox _chkGuid;
        private TextBox _txtStagingPrefix;
        private TextBox _txtGuidPrefix;
        private TextBox _txtMatchKey;
        private ComboBox _cboStagingMode;
        private ComboBox _cboGuidMode;
        private CheckBox _chkTruncate;
        private CheckBox _chkIndexes;
        private CheckBox _chkTeardown;
        private CheckBox _chkManifest;
        private CheckBox _chkMermaid;
        private CheckBox _chkJsonManifest;
        private CheckBox _chkMetaSeed;
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

            _tip = new ToolTip { AutoPopDelay = 15000 };   // some of these take more than 5 seconds to read

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

            var pnlGenerate = new Panel { Dock = DockStyle.Right, Width = 190, Padding = new Padding(14, 18, 0, 4) };

            _btnGenerate = new Button
            {
                Text = "Generate Scripts",
                Font = new Font(Font, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Fill
            };
            _btnGenerate.FlatAppearance.BorderSize = 0;
            _btnGenerate.Click += (s, e) => ExecuteMethod(GenerateScripts);

            var lblStep4 = new Label
            {
                Text = "Step 4",
                Dock = DockStyle.Top,
                Height = 16,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 84, 153)
            };

            pnlGenerate.Controls.Add(_btnGenerate);
            pnlGenerate.Controls.Add(lblStep4);
            _btnGenerate.BringToFront();

            var grpStep3 = new GroupBox
            {
                Text = "Step 3  -  Filter the table list and choose the outputs",
                Dock = DockStyle.Fill
            };

            // Row 1: grid filters and SQL options
            var lblFilter = new Label { Text = "Filter:", Location = new Point(10, 27), AutoSize = true };
            _txtFilter = new TextBox { Location = new Point(52, 24), Width = 160 };

            var lblCategory = new Label { Text = "Category:", Location = new Point(224, 27), AutoSize = true };
            _cboCategory = new ComboBox
            {
                Location = new Point(286, 23),
                Width = 90,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboCategory.Items.Add("All");   // real prefixes are added after Load Tables
            _cboCategory.SelectedIndex = 0;

            _chkCheckedOnly = new CheckBox { Text = "Checked only", Location = new Point(388, 26), AutoSize = true };

            var lblSchema = new Label { Text = "Schema:", Location = new Point(500, 27), AutoSize = true };
            _txtSchema = new TextBox { Location = new Point(556, 24), Width = 55 };

            var lblBatch = new Label { Text = "Batch:", Location = new Point(625, 27), AutoSize = true };
            _numBatch = new NumericUpDown
            {
                Location = new Point(669, 24),
                Width = 55,
                Minimum = 1,
                Maximum = 500,
                Value = 40
            };

            // Row 2: table scripts
            var lblTableScripts = new Label
            {
                Text = "Table scripts:",
                Location = new Point(10, 56),
                AutoSize = true,
                ForeColor = Color.DimGray
            };

            _chkStaging = new CheckBox { Text = "Staging", Location = new Point(96, 55), AutoSize = true, Checked = true };
            _txtStagingPrefix = new TextBox { Location = new Point(166, 52), Width = 62 };
            _cboStagingMode = NewModeCombo(new Point(232, 52));

            _chkGuid = new CheckBox { Text = "GUID", Location = new Point(374, 55), AutoSize = true, Checked = true };
            _txtGuidPrefix = new TextBox { Location = new Point(430, 52), Width = 62 };
            _cboGuidMode = NewModeCombo(new Point(496, 52));

            var lblMatchKey = new Label { Text = "Match key:", Location = new Point(640, 56), AutoSize = true };
            _txtMatchKey = new TextBox { Location = new Point(708, 52), Width = 84 };

            _chkIndexes = new CheckBox { Text = "Index match keys", Location = new Point(802, 55), AutoSize = true };

            // Row 3: extra outputs
            var lblExtras = new Label
            {
                Text = "Extra outputs:",
                Location = new Point(10, 85),
                AutoSize = true,
                ForeColor = Color.DimGray
            };

            _chkTruncate = new CheckBox { Text = "Truncate", Location = new Point(96, 84), AutoSize = true };
            _chkTeardown = new CheckBox { Text = "Teardown", Location = new Point(174, 84), AutoSize = true };
            _chkManifest = new CheckBox { Text = "Data dictionary", Location = new Point(258, 84), AutoSize = true };
            _chkMermaid = new CheckBox { Text = "Mermaid diagram", Location = new Point(372, 84), AutoSize = true };
            _chkJsonManifest = new CheckBox { Text = "Manifest JSON", Location = new Point(496, 84), AutoSize = true, Checked = true };
            _chkMetaSeed = new CheckBox { Text = "Harness metadata", Location = new Point(604, 84), AutoSize = true };

            grpStep3.Controls.AddRange(new Control[]
            {
                lblFilter, _txtFilter, lblCategory, _cboCategory, _chkCheckedOnly,
                lblSchema, _txtSchema, lblBatch, _numBatch,
                lblTableScripts, _chkStaging, _txtStagingPrefix, _cboStagingMode,
                _chkGuid, _txtGuidPrefix, _cboGuidMode, lblMatchKey, _txtMatchKey, _chkIndexes,
                lblExtras, _chkTruncate, _chkTeardown, _chkManifest, _chkMermaid,
                _chkJsonManifest, _chkMetaSeed
            });

            pnlSteps34.Controls.Add(grpStep3);
            pnlSteps34.Controls.Add(pnlGenerate);
            grpStep3.BringToFront();   // index 0 docks last, so Fill claims what Right leaves

            // ---- Tooltips -------------------------------------------------------------
            _tip.SetToolTip(btnLoadTables, "Retrieve the table and solution lists from the connected environment (also clears the session metadata cache)");
            _tip.SetToolTip(_cboSolution, "Tables and fields are filtered to this solution's components (Default = everything)");
            _tip.SetToolTip(btnExclusions, "Field names whose lookups are ignored when ranking tables by dependency");
            _tip.SetToolTip(btnOutputFolder, "Choose where generated files are written - without an output folder, generation is preview-only");
            _tip.SetToolTip(_lblOutputFolder, "Generated files are written here");

            _tip.SetToolTip(_txtStagingPrefix, "Table name prefix, e.g. stage_ or custom_");
            _tip.SetToolTip(_txtGuidPrefix, "Table name prefix for GUID mapping tables");
            _tip.SetToolTip(_txtMatchKey, "Comma-separated column-name suffixes identifying match-key columns (carried into guid tables, indexed by 'Index match keys')");
            _tip.SetToolTip(_chkTruncate, "truncate.sql - truncates all staging tables (guid truncates commented out)");
            _tip.SetToolTip(_chkTeardown, "teardown.sql - drops all staging tables (guid drops commented out)");
            _tip.SetToolTip(_chkManifest, "data_dictionary.xlsx - one sheet per table, ordered by display name, plus an index sheet");
            _tip.SetToolTip(_chkMermaid, "diagram.mmd - Mermaid flowchart of lookup dependencies grouped by tier (render at mermaid.live)");
            _tip.SetToolTip(_chkJsonManifest, "manifest.json - machine-readable run manifest: tables, tiers, file assignments, columns with types, lookup targets, match keys, cycle members");
            _tip.SetToolTip(_chkMetaSeed, "meta_seed.sql - populates meta.Entity and meta.ColumnMap, the harness metadata an SSIS package generator reads. Cycle members get a second PassNo = 2 row carrying only their deferred lookups. Rerunnable: structure is refreshed, hand-tuned values are kept");

            _tip.SetToolTip(_txtFilter, "Filter the table grid by logical or display name");
            _tip.SetToolTip(_cboCategory, "Filter by publisher prefix parsed from the logical name (\"oob\" = no prefix / out-of-box)");
            _tip.SetToolTip(_chkCheckedOnly, "Show only the tables currently checked for generation");
            _tip.SetToolTip(_txtSchema, "SQL schema for the generated tables (default: dbo)");
            _tip.SetToolTip(_numBatch, "Maximum tables per .sql file. Files never mix dependency tiers - a tier larger than this splits into parts, a smaller tier gets its own shorter file");
            _tip.SetToolTip(_btnGenerate, "Retrieve metadata for every checked table, rank tables by lookup dependency, and produce the selected outputs");
            _tip.SetToolTip(_chkStaging, "Generate NN_create_staging.sql files: one column per Dataverse attribute, one dependency tier per file");
            _tip.SetToolTip(_cboStagingMode, "Drop & recreate = DROP IF EXISTS + CREATE (rebuild at will). Create if missing = existing tables are left untouched");
            _tip.SetToolTip(_chkGuid, "Generate NN_create_guid.sql files: id, primary name, match-key and lookup columns - for resolving legacy keys to Dataverse ids during the load");
            _tip.SetToolTip(_cboGuidMode, "Create if missing (default) protects id mappings accumulated across migration runs; Drop & recreate rebuilds them from scratch");
            _tip.SetToolTip(_chkIndexes, "Add a guarded nonclustered index on every match-key column - speeds up the resolution joins during data loads");

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
            _tip.SetToolTip(_lstFiles, "Files produced by the last generation - select one to preview it below");

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

            var rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            var lblFiles = new Label { Dock = DockStyle.Top, Height = 18, Text = "Generated files (select to preview):" };
            rightSplit.Panel1.Controls.Add(_lstFiles);
            rightSplit.Panel1.Controls.Add(lblFiles);
            rightSplit.Panel2.Controls.Add(_txtPreview);
            rightSplit.Panel2.Controls.Add(_txtWarnings);

            var mainSplit = new SplitContainer { Dock = DockStyle.Fill };
            mainSplit.Panel1.Controls.Add(_grid);
            mainSplit.Panel2.Controls.Add(rightSplit);

            // ---- Status bar ----------------------------------------------------------
            var status = new StatusStrip();
            _sslOrg = new ToolStripStatusLabel("(not connected)");
            _sslChecked = new ToolStripStatusLabel("Checked: 0");
            _sslLast = new ToolStripStatusLabel("");
            _sslOutput = new ToolStripStatusLabel("(no output folder - preview only)") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
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
                "     Table scripts   staging and GUID mapping DDL, with editable",
                "                     prefixes and drop-vs-create handling.",
                "     Extra outputs   truncate and teardown scripts, Excel data",
                "                     dictionary, Mermaid diagram, manifest JSON, and",
                "                     the harness metadata seed (meta.Entity and",
                "                     meta.ColumnMap) for a package generator.",
                "",
                "  STEP 4  Generate Scripts",
                "",
                "  Files are batched strictly by dependency tier: everything in a file",
                "  depends only on tables from the same or earlier files - matching",
                "  SSIS/ETL packages organized by load order.",
                "",
                "  Hover any control for details. Full documentation: Help menu or the",
                "  project website (GitHub)."
            });
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

            _chkStaging.Checked = _settings.GenerateStaging;
            _chkGuid.Checked = _settings.GenerateGuid;
            _txtStagingPrefix.Text = _settings.StagingPrefix ?? "stage_";
            _txtGuidPrefix.Text = _settings.GuidPrefix ?? "guid_";
            _txtMatchKey.Text = _settings.MatchKeySuffixes ?? "legacyid";
            _cboStagingMode.SelectedIndex = _settings.StagingDropRecreate ? 0 : 1;
            _cboGuidMode.SelectedIndex = _settings.GuidDropRecreate ? 0 : 1;
            _chkTruncate.Checked = _settings.GenerateTruncateScript;
            _chkIndexes.Checked = _settings.IndexLegacyIdColumns;
            _chkTeardown.Checked = _settings.GenerateTeardown;
            _chkManifest.Checked = _settings.GenerateDataDictionary;
            _chkMermaid.Checked = _settings.GenerateMermaid;
            _chkJsonManifest.Checked = _settings.GenerateJsonManifest;
            _chkMetaSeed.Checked = _settings.GenerateMetadataSeed;

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
            _settings.GenerateStaging = _chkStaging.Checked;
            _settings.GenerateGuid = _chkGuid.Checked;
            _settings.StagingPrefix = _txtStagingPrefix.Text == null ? "" : _txtStagingPrefix.Text.Trim();
            _settings.GuidPrefix = _txtGuidPrefix.Text == null ? "" : _txtGuidPrefix.Text.Trim();
            _settings.MatchKeySuffixes = _txtMatchKey.Text == null ? "" : _txtMatchKey.Text.Trim();
            _settings.StagingDropRecreate = _cboStagingMode.SelectedIndex == 0;
            _settings.GuidDropRecreate = _cboGuidMode.SelectedIndex == 0;
            _settings.GenerateTruncateScript = _chkTruncate.Checked;
            _settings.IndexLegacyIdColumns = _chkIndexes.Checked;
            _settings.GenerateTeardown = _chkTeardown.Checked;
            _settings.GenerateDataDictionary = _chkManifest.Checked;
            _settings.GenerateMermaid = _chkMermaid.Checked;
            _settings.GenerateJsonManifest = _chkJsonManifest.Checked;
            _settings.GenerateMetadataSeed = _chkMetaSeed.Checked;
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

        private void GenerateScripts()
        {
            CaptureSettingsFromUi();
            ExitEditMode();

            // Only generate for tables that exist in the connected environment AND are part
            // of the currently selected solution.
            var known = new HashSet<string>(
                _allTables.Where(InCurrentSolution).Select(t => t.LogicalName),
                StringComparer.OrdinalIgnoreCase);
            var picks = _checkedTables.Where(t => known.Contains(t))
                                      .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                                      .ToList();

            if (picks.Count == 0)
            {
                MessageBox.Show(this, "Load tables and check at least one first.",
                    "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_settings.GenerateStaging && !_settings.GenerateGuid && !_settings.GenerateTruncateScript &&
                !_settings.GenerateTeardown && !_settings.GenerateDataDictionary && !_settings.GenerateMermaid &&
                !_settings.GenerateJsonManifest && !_settings.GenerateMetadataSeed)
            {
                MessageBox.Show(this, "Enable at least one output.",
                    "Nothing to generate", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var settings = _settings;
            var cache = _metadataCache;
            var solutionFilter = _solutionFilter;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Retrieving attribute metadata...",
                IsCancelable = true,
                Work = (worker, args) =>
                {
                    var service = new MetadataService(Service);
                    var tables = new List<TableModel>();

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

                        tables.Add(MetadataMapper.BuildTable(entity, settings, solutionFilter));
                    }

                    worker.ReportProgress(100, "Generating scripts...");
                    var generator = new ScriptGenerator(settings);
                    args.Result = generator.Generate(tables);
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

                    _lastResult = (GenerationResult)args.Result;
                    ShowResult(_lastResult);
                    SaveSettings();   // persist the selection that produced this run
                }
            });
        }

        private void ShowResult(GenerationResult result)
        {
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

            var written = 0;
            if (!string.IsNullOrEmpty(_settings.OutputFolder))
            {
                try
                {
                    Directory.CreateDirectory(_settings.OutputFolder);
                    foreach (var file in result.Files)
                    {
                        var path = Path.Combine(_settings.OutputFolder, file.FileName);
                        if (file.BinaryContent != null)
                        {
                            File.WriteAllBytes(path, file.BinaryContent);
                        }
                        else
                        {
                            File.WriteAllText(path, file.Content, Encoding.UTF8);
                        }
                        written++;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Scripts were generated but writing files failed: " + ex.Message,
                        "Write error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            _sslLast.Text = string.Format("Last run: {0} tables -> {1} files at {2:HH:mm}",
                result.OrderedTables.Count, result.Files.Count, DateTime.Now);

            var summary = new StringBuilder();
            summary.AppendFormat("{0} tables -> {1} files.", result.OrderedTables.Count, result.Files.Count);
            summary.AppendLine();
            summary.AppendLine(written > 0
                ? string.Format("{0} files written to {1}", written, _settings.OutputFolder)
                : "No output folder set - use Set Output Folder to write files to disk.");
            if (result.Warnings.Count > 0)
            {
                summary.AppendFormat("{0} warning(s) - see panel below the preview.", result.Warnings.Count);
            }

            MessageBox.Show(this, summary.ToString(), "Generation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowSelectedFile()
        {
            if (_lastResult == null || _lstFiles.SelectedIndex < 0) return;
            _txtPreview.Text = _lastResult.Files[_lstFiles.SelectedIndex].Content;
        }

        #endregion
    }
}
