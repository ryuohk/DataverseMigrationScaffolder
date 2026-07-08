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
        private ToolStrip _toolStrip;
        private ToolStripComboBox _cboSolution;
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

            // ---- Row 1: actions ----------------------------------------------------
            _toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

            var tsbLoadTables = new ToolStripButton("Load Tables") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            tsbLoadTables.Click += (s, e) => ExecuteMethod(LoadTables);

            _cboSolution = new ToolStripComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 240,
                ToolTipText = "Tables and fields are filtered to this solution's components (Default = everything)"
            };
            _cboSolution.SelectedIndexChanged += (s, e) => OnSolutionSelectionChanged();

            var tsbExclusions = new ToolStripButton("Dependency Exclusions") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoSize = true, ToolTipText = "Field names whose lookups are ignored when ranking tables by dependency" };
            tsbExclusions.Click += (s, e) => EditDependencyExclusions();

            var tsbOutputFolder = new ToolStripButton("Set Output Folder") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoSize = true };
            tsbOutputFolder.Click += (s, e) => PickOutputFolder();

            _toolStrip.Items.AddRange(new ToolStripItem[]
            {
                tsbLoadTables,
                new ToolStripSeparator(),
                new ToolStripLabel("Solution:"), _cboSolution,
                new ToolStripSeparator(),
                tsbExclusions,
                new ToolStripSeparator(),
                tsbOutputFolder
            });

            // ---- Row 2: filters + options + generate -------------------------------
            var pnlOptions = new Panel { Dock = DockStyle.Top, Height = 44 };

            var lblFilter = new Label { Text = "Filter:", Location = new Point(10, 14), AutoSize = true };
            _txtFilter = new TextBox { Location = new Point(52, 11), Width = 170 };

            var lblCategory = new Label { Text = "Category:", Location = new Point(238, 14), AutoSize = true };
            _cboCategory = new ComboBox
            {
                Location = new Point(298, 10),
                Width = 90,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cboCategory.Items.Add("All");   // real prefixes are added after Load Tables
            _cboCategory.SelectedIndex = 0;

            _chkCheckedOnly = new CheckBox { Text = "Checked only", Location = new Point(400, 13), AutoSize = true };

            var lblSchema = new Label { Text = "Schema:", Location = new Point(510, 14), AutoSize = true };
            _txtSchema = new TextBox { Location = new Point(564, 11), Width = 55 };

            var lblBatch = new Label { Text = "Batch:", Location = new Point(632, 14), AutoSize = true };
            _numBatch = new NumericUpDown
            {
                Location = new Point(674, 11),
                Width = 55,
                Minimum = 1,
                Maximum = 500,
                Value = 40
            };

            _btnGenerate = new Button
            {
                Text = "Generate Scripts",
                Font = new Font(Font, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(170, 32),
                Location = new Point(750, 6)
            };
            _btnGenerate.FlatAppearance.BorderSize = 0;
            _btnGenerate.Click += (s, e) => ExecuteMethod(GenerateScripts);

            pnlOptions.Controls.AddRange(new Control[]
            {
                lblFilter, _txtFilter, lblCategory, _cboCategory, _chkCheckedOnly,
                lblSchema, _txtSchema, lblBatch, _numBatch, _btnGenerate
            });

            // ---- Row 3: output options ----------------------------------------------
            var pnlOutput = new Panel { Dock = DockStyle.Top, Height = 58 };

            var grpTables = new GroupBox { Text = "Table scripts", Location = new Point(8, 2), Size = new Size(880, 52) };

            _chkStaging = new CheckBox { Text = "Staging", Location = new Point(10, 20), AutoSize = true, Checked = true };
            _txtStagingPrefix = new TextBox { Location = new Point(80, 17), Width = 65, Text = "stage_" };
            _cboStagingMode = NewModeCombo(new Point(150, 17));

            _chkGuid = new CheckBox { Text = "GUID", Location = new Point(305, 20), AutoSize = true, Checked = true };
            _txtGuidPrefix = new TextBox { Location = new Point(360, 17), Width = 65, Text = "guid_" };
            _cboGuidMode = NewModeCombo(new Point(430, 17));

            var lblMatchKey = new Label { Text = "Match key:", Location = new Point(585, 21), AutoSize = true };
            _txtMatchKey = new TextBox { Location = new Point(650, 17), Width = 90, Text = "legacyid" };

            _chkIndexes = new CheckBox { Text = "Index match keys", Location = new Point(750, 20), AutoSize = true };

            grpTables.Controls.AddRange(new Control[]
            {
                _chkStaging, _txtStagingPrefix, _cboStagingMode,
                _chkGuid, _txtGuidPrefix, _cboGuidMode,
                lblMatchKey, _txtMatchKey, _chkIndexes
            });

            var grpExtras = new GroupBox { Text = "Extra outputs", Location = new Point(898, 2), Size = new Size(290, 52) };

            _chkTruncate = new CheckBox { Text = "Truncate script", Location = new Point(10, 13), AutoSize = true };
            _chkTeardown = new CheckBox { Text = "Teardown script", Location = new Point(10, 31), AutoSize = true };
            _chkManifest = new CheckBox { Text = "Data dictionary", Location = new Point(145, 13), AutoSize = true };
            _chkMermaid = new CheckBox { Text = "Mermaid diagram", Location = new Point(145, 31), AutoSize = true };

            grpExtras.Controls.AddRange(new Control[] { _chkTruncate, _chkTeardown, _chkManifest, _chkMermaid });

            var tip = new ToolTip();
            tip.SetToolTip(_txtStagingPrefix, "Table name prefix, e.g. stage_ or custom_");
            tip.SetToolTip(_txtGuidPrefix, "Table name prefix for GUID mapping tables");
            tip.SetToolTip(_txtMatchKey, "Comma-separated column-name suffixes identifying match-key columns (carried into guid tables, indexed by 'Index match keys')");
            tip.SetToolTip(_chkTruncate, "truncate.sql - truncates all staging tables (guid truncates commented out)");
            tip.SetToolTip(_chkTeardown, "teardown.sql - drops all staging tables (guid drops commented out)");
            tip.SetToolTip(_chkManifest, "data_dictionary.xlsx - one sheet per table, ordered by display name, plus an index sheet");
            tip.SetToolTip(_chkMermaid, "diagram.mmd - Mermaid flowchart of lookup dependencies grouped by tier (render at mermaid.live)");

            pnlOutput.Controls.Add(grpTables);
            pnlOutput.Controls.Add(grpExtras);

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

            _txtPreview = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 9f)
            };

            _txtWarnings = new TextBox
            {
                Dock = DockStyle.Bottom,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Height = 80,
                ForeColor = Color.DarkRed
            };

            var rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            rightSplit.Panel1.Controls.Add(_lstFiles);
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

            Controls.Add(mainSplit);
            Controls.Add(pnlOutput);
            Controls.Add(pnlOptions);
            Controls.Add(_toolStrip);
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

        private static ComboBox NewModeCombo(Point location)
        {
            var combo = new ComboBox
            {
                Location = location,
                Width = 140,
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
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            SaveSettings();
            base.ClosingPlugin(info);
        }

        private void UpdateOutputFolderLabel()
        {
            _sslOutput.Text = string.IsNullOrEmpty(_settings.OutputFolder)
                ? "(no output folder - preview only)"
                : _settings.OutputFolder;
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
                !_settings.GenerateTeardown && !_settings.GenerateDataDictionary && !_settings.GenerateMermaid)
            {
                MessageBox.Show(this, "Enable at least one output (Staging, GUID, Truncate, Teardown, Data dictionary or Mermaid).",
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

            _txtWarnings.Text = result.Warnings.Count == 0
                ? ""
                : "Warnings:" + Environment.NewLine + string.Join(Environment.NewLine, result.Warnings);

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
