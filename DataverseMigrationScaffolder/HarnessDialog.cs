using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DataverseMigrationScaffolder.Core;

namespace DataverseMigrationScaffolder
{
    /// <summary>
    /// SSIS Settings: the template (the built-in one with its connections, or the user's own
    /// reference project and template package) and the new project (name, protection level),
    /// remembered in ToolSettings. Generate closes the dialog with OK and the main control generates
    /// from the tables checked in the session; "Build from a previous run" generates here from a
    /// manifest.json and the scripts next to it.
    /// </summary>
    public sealed class HarnessDialog : Form
    {
        private const int LabelWidth = 150;
        private const int FieldWidth = 560;
        private const int SectionWidth = 830;

        private readonly RadioButton builtIn = new RadioButton { Text = "Built-in template (nothing to provide)", AutoSize = true };
        private readonly RadioButton ownTemplate = new RadioButton { Text = "My own reference project", AutoSize = true };
        private readonly Label templateNote = Note();
        private readonly TextBox sqlServer = new TextBox { Width = 240 };
        private readonly TextBox stagingDatabase = new TextBox { Width = 180 };
        private readonly TextBox legacyDatabase = new TextBox { Width = 180 };
        private readonly TextBox dataverseUrl = new TextBox { Width = FieldWidth };
        private readonly Label urlNote = Note("from your XrmToolBox connection");
        private readonly TextBox reference = new TextBox { Width = FieldWidth };
        private readonly Button referenceBrowse = new Button { Text = "Browse...", AutoSize = true };
        private readonly ComboBox projects = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = FieldWidth };
        private readonly ComboBox packages = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly TextBox projectName = new TextBox { Width = 260 };
        private readonly TextBox output = new TextBox { Width = FieldWidth, ReadOnly = true };
        private readonly Button outputChange = new Button { Text = "Change...", AutoSize = true };
        private readonly CheckBox dontSaveSensitive = new CheckBox { Text = "Don't save passwords or secrets in the project (DontSaveSensitive)", AutoSize = true };
        private readonly TextBox manifest = new TextBox { Width = FieldWidth };
        private readonly Button manifestBrowse = new Button { Text = "Browse...", AutoSize = true };
        private readonly TextBox results = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                                                         Width = LabelWidth + FieldWidth + 90, Height = 180, Visible = false };
        private readonly GroupBox previousRun = new GroupBox { Text = "Build from a previous run", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                                               MinimumSize = new Size(SectionWidth, 0), Margin = new Padding(0, 0, 0, 8) };
        private readonly LinkLabel modeLink = new LinkLabel { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        private readonly Label status = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(LabelWidth + FieldWidth + 90, 0),
                                                    Margin = new Padding(3, 6, 3, 0) };
        private readonly Button generate = new Button { Text = "Generate", AutoSize = true, MinimumSize = new Size(90, 0) };
        private readonly Button save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 0) };
        private readonly Button cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        private readonly Dictionary<Control, Label> labels = new Dictionary<Control, Label>();
        private readonly ToolSettings settings;
        private readonly string connectedUrl;
        private readonly bool sessionAvailable;
        private bool fromPreviousRun;
        private bool busy;
        private ToolTip tip;

        /// <summary>The output folder chosen for a generation from this session.</summary>
        public string OutputFolder { get { return output.Text.Trim(); } }

        public HarnessDialog(ToolSettings settings, bool sessionAvailable, string connectedUrl = null)
        {
            this.settings = settings;
            this.connectedUrl = connectedUrl;
            this.sessionAvailable = sessionAvailable;
            Text = "SSIS Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            StartPosition = FormStartPosition.CenterParent;
            Padding = new Padding(10);
            AcceptButton = generate;
            CancelButton = cancel;

            // ---- Template ----------------------------------------------------------------------
            var templateRows = Rows();
            var choice = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            choice.Controls.Add(builtIn);
            choice.Controls.Add(ownTemplate);
            AddRow(templateRows, "Use", choice);
            AddRow(templateRows, "", templateNote);
            AddRow(templateRows, "SQL Server", sqlServer);
            AddRow(templateRows, "Staging database", stagingDatabase);
            AddRow(templateRows, "Legacy database", legacyDatabase);
            AddRow(templateRows, "Dataverse URL", dataverseUrl);
            AddRow(templateRows, "", urlNote);
            AddRow(templateRows, "Reference project/solution", reference, referenceBrowse);
            AddRow(templateRows, "SSIS project", projects);
            AddRow(templateRows, "Template package", packages);
            referenceBrowse.Click += (s, e) => { if (PickFile(reference, "SSIS project or solution|*.dtproj;*.sln")) LoadProjects(); };

            // ---- New project ---------------------------------------------------------------------
            var projectRows = Rows();
            AddRow(projectRows, "Project name", projectName);
            AddRow(projectRows, "Output folder", output, outputChange);
            AddRow(projectRows, "Sensitive data", dontSaveSensitive);
            outputChange.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "Choose an empty folder for the new SSIS project", ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath;
            };

            // ---- Build from a previous run (hidden until chosen) ------------------------------------
            var runRows = Rows();
            AddRow(runRows, "", Note("Rebuild from the Scaffolder folder of an earlier SSIS project, or from files saved with Export > "
                                     + "Scaffolder run. No Dataverse connection is needed."));
            AddRow(runRows, "Scaffolder manifest", manifest, manifestBrowse);
            runRows.Controls.Add(results, 0, runRows.RowCount);
            runRows.SetColumnSpan(results, 3);
            runRows.RowCount++;
            previousRun.Controls.Add(runRows);
            manifestBrowse.Click += (s, e) => PickFile(manifest, "Scaffolder manifest|manifest.json|JSON files|*.json",
                ManifestBrowseFolder(manifest.Text, settings.OutputFolder), "manifest.json");

            // ---- Buttons -------------------------------------------------------------------------
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right,
                                                Margin = new Padding(0), WrapContents = false };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            buttons.Controls.Add(generate);
            var footer = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(modeLink, 0, 0);
            footer.Controls.Add(buttons, 1, 0);

            var page = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Location = new Point(10, 10) };
            page.Controls.Add(Section("Template", templateRows));
            page.Controls.Add(Section("New project", projectRows));
            page.Controls.Add(previousRun);
            page.Controls.Add(status);
            page.Controls.Add(footer);
            Controls.Add(page);
            AddTips();

            // ---- Remembered settings --------------------------------------------------------------
            sqlServer.Text = settings.HarnessSqlServer ?? "";
            stagingDatabase.Text = settings.HarnessStagingDatabase ?? "";
            legacyDatabase.Text = settings.HarnessLegacyDatabase ?? "";
            dataverseUrl.Text = !string.IsNullOrWhiteSpace(settings.HarnessDataverseUrl) ? settings.HarnessDataverseUrl : connectedUrl ?? "";
            reference.Text = settings.HarnessReference ?? "";
            projectName.Text = string.IsNullOrWhiteSpace(settings.HarnessProjectName) ? "MigrationHarness_Generated" : settings.HarnessProjectName;
            dontSaveSensitive.Checked = settings.HarnessDontSaveSensitive;
            if (!string.IsNullOrWhiteSpace(settings.OutputFolder))
            {
                manifest.Text = HarnessGenerator.LatestRunManifest(settings.OutputFolder);
                output.Text = HarnessGenerator.NewOutputFolder(settings.OutputFolder);
            }
            (settings.UsesBuiltInTemplate ? builtIn : ownTemplate).Checked = true;
            projects.SelectedIndexChanged += (s, e) => LoadPackages();
            builtIn.CheckedChanged += (s, e) => UpdateTemplate();
            dataverseUrl.TextChanged += (s, e) => UpdateUrlNote();
            reference.Leave += (s, e) => LoadProjects();
            LoadProjects();
            UpdateTemplate();
            UpdateUrlNote();

            fromPreviousRun = !sessionAvailable;
            modeLink.LinkClicked += (s, e) => { fromPreviousRun = !fromPreviousRun; UpdateMode(); };
            UpdateMode();

            save.Click += (s, e) => { Remember(); DialogResult = DialogResult.Ignore; Close(); };
            generate.Click += async (s, e) => await OnGenerate();
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
        }

        private async System.Threading.Tasks.Task OnGenerate()
        {
            Remember();
            if (!fromPreviousRun)
            {
                var problem = builtIn.Checked
                    ? HarnessGenerator.BuiltInProblem(sqlServer.Text, stagingDatabase.Text, legacyDatabase.Text, OutputFolder, projectName.Text.Trim())
                    : HarnessGenerator.Problem((string)projects.SelectedItem, (string)packages.SelectedItem, OutputFolder, projectName.Text.Trim());
                if (problem != null) { ShowStatus(problem); return; }
                DialogResult = DialogResult.OK;   // the main control generates from the session's tables
                Close();
                return;
            }
            busy = true;
            Enable(false);
            ShowStatus("");
            results.Visible = true;
            results.Text = "Generating the project...";
            try
            {
                using (var template = HarnessGenerator.ResolveTemplate(settings, connectedUrl))
                    results.Text = await HarnessGenerator.Generate(manifest.Text.Trim(), template.ProjectFile, template.Package,
                                                                   OutputFolder, projectName.Text.Trim(), dontSaveSensitive.Checked);
                // A further build needs a new, empty folder.
                if (!string.IsNullOrWhiteSpace(settings.OutputFolder)) output.Text = HarnessGenerator.NewOutputFolder(settings.OutputFolder);
            }
            catch (Exception ex)
            {
                results.Text = "";
                results.Visible = false;
                ShowStatus("Generation failed: " + ex.Message);
            }
            finally { busy = false; Enable(true); }
        }

        private void Enable(bool enabled)
        {
            foreach (Control control in Controls) control.Enabled = enabled;
        }

        /// <summary>Show the built-in template's connection fields or the reference project fields.</summary>
        private void UpdateTemplate()
        {
            var own = !builtIn.Checked;
            foreach (var field in new Control[] { sqlServer, stagingDatabase, legacyDatabase, dataverseUrl })
                SetRowVisible(field, !own);
            foreach (var field in new Control[] { reference, projects, packages })
                SetRowVisible(field, own);
            referenceBrowse.Visible = own;
            templateNote.Text = own
                ? "Copies a hand-built SSIS project of yours: its connections, settings and template package."
                : "Needs only your connections. Every table and column comes from your Dataverse environment.";
            UpdateUrlNote();
            if (own && projects.Items.Count == 0) LoadProjects();   // shows a broken reference path now
            else if (!own) ShowStatus("");
        }

        private void UpdateUrlNote()
        {
            urlNote.Visible = builtIn.Checked && !string.IsNullOrEmpty(connectedUrl)
                              && string.Equals(dataverseUrl.Text.Trim().TrimEnd('/'), connectedUrl, StringComparison.OrdinalIgnoreCase);
            if (labels.ContainsKey(urlNote)) labels[urlNote].Visible = urlNote.Visible;
        }

        private void UpdateMode()
        {
            previousRun.Visible = fromPreviousRun;
            modeLink.Visible = sessionAvailable;
            modeLink.Text = fromPreviousRun ? "Use the tables checked in this session" : "Build from a previous run...";
            if (!fromPreviousRun) results.Visible = false;
            if (!sessionAvailable) ShowStatus("No tables are checked in the main window, so the project is built from a previous run.", false);
        }

        private void ShowStatus(string text, bool problem = true)
        {
            status.ForeColor = problem ? Color.Firebrick : SystemColors.GrayText;
            status.Text = text ?? "";
            status.Visible = status.Text.Length > 0;
        }

        private void SetRowVisible(Control field, bool visible)
        {
            field.Visible = visible;
            Label label;
            if (labels.TryGetValue(field, out label)) label.Visible = visible;
        }

        private void AddTips()
        {
            tip = Tips.New();
            Tips.Set(tip, "Built-in template: the plugin's own reference project, a migration flow for one sample table with the "
                + "recommended KingswaySoft settings, staging SQL and error logging. Nothing to provide except the connections below; "
                + "every table and column in the generated project comes from your Dataverse environment.\n"
                + "My own reference project: copy a hand-built SSIS project of yours instead, with its connections and settings.",
                Label(builtIn.Parent), builtIn, ownTemplate);
            Tips.Set(tip, "The SQL Server instance that holds the staging and legacy databases, e.g. localhost or MYSERVER\\SQLEXPRESS. "
                + "Written into the generated project's Staging and Legacy connections (Windows authentication).", Label(sqlServer), sqlServer);
            Tips.Set(tip, "The database the staging and GUID tables are created in.", Label(stagingDatabase), stagingDatabase);
            Tips.Set(tip, "The database holding the legacy data. The generated Stage SQL reads it by name ([<database>].[dbo].[<table>]), "
                + "so it must be on the same SQL Server as the staging database.", Label(legacyDatabase), legacyDatabase);
            Tips.Set(tip, "The Dataverse environment the generated project loads into. Defaults to the environment XrmToolBox is connected "
                + "to. Enter the client id and secret in Visual Studio after opening the project; they are never stored by the plugin.",
                Label(dataverseUrl), dataverseUrl, urlNote);
            Tips.Set(tip, "Your hand-built SSIS solution (.sln) or project (.dtproj) to copy from. Its connection managers, project "
                + "parameters and package settings are reused. It is only read, never changed.", Label(reference), reference, referenceBrowse);
            Tips.Set(tip, "The SSIS project inside that solution. For a .dtproj reference this is the project itself.", Label(projects), projects);
            Tips.Set(tip, "The package every generated package is patterned on. It must contain one Migrate data flow with a KingswaySoft "
                + "destination. Its settings, including the checkboxes that disable plugins, workflows and auditing during the load, "
                + "are copied to every table, and so is its Stage SQL task if it has one.", Label(packages), packages);
            Tips.Set(tip, "The name of the new project: its .dtproj file and the name Visual Studio shows.", Label(projectName), projectName);
            Tips.Set(tip, "The folder the new project is written to: a new SSIS-<date-time> folder in the scaffolder's output folder, made "
                + "fresh for every generation. Use Change... to pick another empty folder.", Label(output), output, outputChange);
            Tips.Set(tip, "Unticked: the new project keeps the template's protection level. With the built-in template no secret is stored "
                + "until you enter it in Visual Studio; then it is saved encrypted for you. With your own reference, its encrypted values "
                + "are copied unchanged and only open for the person who saved it.\n"
                + "Ticked: the project is saved with DontSaveSensitive. No password or secret is ever stored, so anyone can open it and it "
                + "is safe to commit. Supply them when deploying, in the SSIS catalog's connection manager settings "
                + "(e.g. CM.<connection manager>.ClientSecret) or a SQL Agent job step, or re-enter them each time you open the project.",
                Label(dontSaveSensitive), dontSaveSensitive);
            Tips.Set(tip, "The manifest.json of the earlier run. Its staging and GUID scripts and meta_seed.sql must be in the same folder.\n"
                + "Defaults to the newest SSIS-*\\Scaffolder\\manifest.json in the output folder.", Label(manifest), manifest, manifestBrowse);
            Tips.Set(tip, "Switch between building from the tables checked in the main window (the usual way) and rebuilding from the "
                + "saved files of an earlier run.", modeLink);
            Tips.Set(tip, "Remember these settings and build the project. Nothing is executed against SQL Server or Dataverse; open and "
                + "build the result in Visual Studio.", generate);
            Tips.Set(tip, "Remember these settings without generating.", save);
            Tips.Set(tip, "Close without saving any changes.", cancel);
            Tips.Set(tip, "Generation results.", results);
        }

        /// <summary>The row label next to a field added with AddRow (the field itself if it has none).</summary>
        private Control Label(Control field)
        {
            Label label;
            return labels.TryGetValue(field, out label) ? (Control)label : field;
        }

        private void Remember()
        {
            settings.HarnessTemplate = builtIn.Checked ? ToolSettings.BuiltInTemplate : ToolSettings.OwnTemplate;
            settings.HarnessSqlServer = sqlServer.Text.Trim();
            settings.HarnessStagingDatabase = stagingDatabase.Text.Trim();
            settings.HarnessLegacyDatabase = legacyDatabase.Text.Trim();
            // The connected environment's URL is not remembered, so the next connection's is used.
            var url = dataverseUrl.Text.Trim().TrimEnd('/');
            settings.HarnessDataverseUrl = string.Equals(url, connectedUrl ?? "", StringComparison.OrdinalIgnoreCase) ? "" : url;
            // The reference project is kept while the built-in template is in use, for switching back.
            if (ownTemplate.Checked)
            {
                settings.HarnessReference = reference.Text.Trim();
                settings.HarnessProjectFile = projects.SelectedItem as string ?? "";
                settings.HarnessPackage = packages.SelectedItem as string ?? "";
            }
            settings.HarnessProjectName = projectName.Text.Trim();
            settings.HarnessDontSaveSensitive = dontSaveSensitive.Checked;
        }

        // ---- layout helpers ------------------------------------------------------------------------

        private static TableLayoutPanel Rows()
        {
            var rows = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3,
                                              Location = new Point(8, 20), Margin = new Padding(0), Padding = new Padding(0) };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, FieldWidth + 8));
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return rows;
        }

        private static GroupBox Section(string title, TableLayoutPanel rows)
        {
            var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 8),
                                     MinimumSize = new Size(SectionWidth, 0) };
            box.Controls.Add(rows);
            return box;
        }

        private static Label Note(string text = "")
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(FieldWidth, 0) };
        }

        private void AddRow(TableLayoutPanel rows, string label, Control field, Control button = null)
        {
            var row = rows.RowCount++;
            if (label.Length > 0)
            {
                var name = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
                rows.Controls.Add(name, 0, row);
                labels[field] = name;
            }
            field.Anchor = AnchorStyles.Left;
            field.Margin = new Padding(3, label.Length > 0 ? 4 : 0, 3, label.Length > 0 ? 4 : 2);
            rows.Controls.Add(field, 1, row);
            if (button != null) rows.Controls.Add(button, 2, row);
        }

        /// <summary>
        /// Where the manifest Browse dialog opens: the folder of the manifest path already
        /// entered, if that folder exists; otherwise the scaffolder's output folder; otherwise
        /// null (Windows' own default).
        /// </summary>
        internal static string ManifestBrowseFolder(string manifestPath, string outputFolder)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(manifestPath))
                {
                    var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath.Trim()));
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder;
                }
            }
            catch (ArgumentException) { }       // invalid characters in a typed path
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return !string.IsNullOrWhiteSpace(outputFolder) && Directory.Exists(outputFolder) ? outputFolder : null;
        }

        private bool PickFile(TextBox target, string filter, string initialFolder = null, string fileName = null)
        {
            using (var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true })
            {
                if (initialFolder != null)
                {
                    dialog.InitialDirectory = initialFolder;
                    // Pre-select the file when it is already there.
                    if (fileName != null && File.Exists(Path.Combine(initialFolder, fileName))) dialog.FileName = fileName;
                }
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                target.Text = dialog.FileName;
                return true;
            }
        }

        private void LoadProjects()
        {
            if (string.IsNullOrWhiteSpace(reference.Text)) return;
            try
            {
                var selected = projects.SelectedItem as string ?? settings.HarnessProjectFile;
                projects.Items.Clear();
                packages.Items.Clear();
                projects.Items.AddRange(HarnessGenerator.Projects(reference.Text.Trim()));
                var match = projects.Items.Cast<string>().FirstOrDefault(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
                projects.SelectedIndex = match != null ? projects.Items.IndexOf(match) : 0;
            }
            catch (Exception ex) { if (ownTemplate.Checked) ShowStatus(ex.Message); }
        }

        private void LoadPackages()
        {
            packages.Items.Clear();
            if (projects.SelectedItem == null) return;
            try
            {
                packages.Items.AddRange(HarnessGenerator.Packages((string)projects.SelectedItem));
                // A multi-package project requires an explicit choice, not a guessed template.
                var remembered = packages.Items.Cast<string>().FirstOrDefault(p => p == settings.HarnessPackage);
                if (remembered != null) packages.SelectedItem = remembered;
                else if (packages.Items.Count == 1) packages.SelectedIndex = 0;
            }
            catch (Exception ex) { if (ownTemplate.Checked) ShowStatus(ex.Message); }
        }
    }
}
