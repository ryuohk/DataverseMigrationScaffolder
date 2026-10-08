using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DataverseMigrationScaffolder.Core;

namespace DataverseMigrationScaffolder
{
    /// <summary>
    /// SSIS project settings: the template (the built-in one with its connections, or the user's own
    /// reference project and template package) and project name (remembered in ToolSettings), and
    /// where the scaffolder outputs come from. "Tables checked in this
    /// session" closes the dialog with OK and the main control generates in memory; "A previous
    /// scaffolder run" generates here from a manifest.json and the scripts next to it.
    /// </summary>
    public sealed class HarnessDialog : Form
    {
        private readonly RadioButton fromSession = new RadioButton { Text = "Tables checked in this session (no files needed)", AutoSize = true };
        private readonly RadioButton fromManifest = new RadioButton { Text = "A previous scaffolder run (manifest.json)", AutoSize = true };
        private readonly TextBox manifest = new TextBox();
        private readonly Button manifestBrowse = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        private readonly RadioButton builtIn = new RadioButton { Text = "Built-in template (nothing to provide)", AutoSize = true };
        private readonly RadioButton ownTemplate = new RadioButton { Text = "My own reference project", AutoSize = true };
        private readonly TextBox sqlServer = new TextBox();
        private readonly TextBox stagingDatabase = new TextBox();
        private readonly TextBox legacyDatabase = new TextBox();
        private readonly TextBox dataverseUrl = new TextBox();
        private readonly TextBox reference = new TextBox();
        private readonly ComboBox projects = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox packages = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox output = new TextBox();
        private readonly TextBox projectName = new TextBox();
        private readonly CheckBox dontSaveSensitive = new CheckBox { Text = "Don't save passwords or secrets in the project (DontSaveSensitive)", AutoSize = true };
        private readonly Label hint = new Label { AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(4, 10, 4, 10) };
        private readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        private readonly Button generate = new Button { AutoSize = true };
        private readonly TableLayoutPanel fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10) };
        private readonly ToolSettings settings;
        private readonly string connectedUrl;
        private Button referenceBrowse;
        private bool busy;
        private ToolTip tip;

        /// <summary>The output folder chosen for a generation from this session.</summary>
        public string OutputFolder { get { return output.Text.Trim(); } }

        public HarnessDialog(ToolSettings settings, bool sessionAvailable, string connectedUrl = null)
        {
            this.settings = settings;
            this.connectedUrl = connectedUrl;
            Text = "Generate SSIS Migration Harness";
            Size = new Size(940, 660);
            MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterParent;
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));

            var source = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(4, 3, 4, 3) };
            source.Controls.Add(fromSession);
            source.Controls.Add(fromManifest);
            AddRow("Generate from", source);
            AddRow("Scaffolder manifest", manifest, manifestBrowse);
            manifestBrowse.Click += (s, e) => PickFile(manifest, "Scaffolder manifest|manifest.json|JSON files|*.json",
                ManifestBrowseFolder(manifest.Text, settings.OutputFolder), "manifest.json");
            var templates = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(4, 3, 4, 3) };
            templates.Controls.Add(builtIn);
            templates.Controls.Add(ownTemplate);
            AddRow("Template", templates);
            AddRow("SQL Server", sqlServer);
            AddRow("Staging database", stagingDatabase);
            AddRow("Legacy database", legacyDatabase);
            AddRow("Dataverse URL", dataverseUrl);
            referenceBrowse = new Button { Text = "Browse...", Dock = DockStyle.Fill };
            referenceBrowse.Click += (s, e) => { if (PickFile(reference, "SSIS project or solution|*.dtproj;*.sln")) LoadProjects(); };
            AddRow("Reference project/solution", reference, referenceBrowse);
            AddRow("SSIS project", projects);
            AddRow("Template package", packages);
            AddRow("New project name", projectName);
            AddRow("Sensitive data", dontSaveSensitive);
            var outputBrowse = new Button { Text = "Browse...", Dock = DockStyle.Fill };
            outputBrowse.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog { Description = "Choose an empty folder for the new SSIS project", ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath;
            };
            AddRow("New output folder", output, outputBrowse);
            fields.Controls.Add(hint, 0, fields.RowCount);
            fields.SetColumnSpan(hint, 3);
            fields.RowCount++;
            fields.Controls.Add(generate, 1, fields.RowCount++);
            log.Dock = DockStyle.Fill;
            AddTips(referenceBrowse, outputBrowse);
            Controls.Add(log);
            Controls.Add(fields);

            // Remembered settings.
            sqlServer.Text = settings.HarnessSqlServer ?? "";
            stagingDatabase.Text = settings.HarnessStagingDatabase ?? "";
            legacyDatabase.Text = settings.HarnessLegacyDatabase ?? "";
            dataverseUrl.Text = !string.IsNullOrWhiteSpace(settings.HarnessDataverseUrl) ? settings.HarnessDataverseUrl : connectedUrl ?? "";
            (settings.UsesBuiltInTemplate ? builtIn : ownTemplate).Checked = true;
            builtIn.CheckedChanged += (s, e) => UpdateTemplate();
            UpdateTemplate();
            reference.Text = settings.HarnessReference ?? "";
            projectName.Text = string.IsNullOrWhiteSpace(settings.HarnessProjectName) ? "MigrationHarness_Generated" : settings.HarnessProjectName;
            dontSaveSensitive.Checked = settings.HarnessDontSaveSensitive;
            if (!string.IsNullOrWhiteSpace(settings.OutputFolder))
            {
                manifest.Text = HarnessGenerator.LatestRunManifest(settings.OutputFolder);
                output.Text = HarnessGenerator.NewOutputFolder(settings.OutputFolder);
            }
            projects.SelectedIndexChanged += (s, e) => LoadPackages();
            LoadProjects();

            fromSession.Enabled = sessionAvailable;
            (sessionAvailable ? fromSession : fromManifest).Checked = true;
            fromSession.CheckedChanged += (s, e) => UpdateMode();
            UpdateMode();
            reference.Leave += (s, e) => LoadProjects();
            generate.Click += async (s, e) =>
            {
                Remember();
                if (fromSession.Checked)
                {
                    var problem = builtIn.Checked
                        ? HarnessGenerator.BuiltInProblem(sqlServer.Text, stagingDatabase.Text, legacyDatabase.Text, OutputFolder, projectName.Text.Trim())
                        : HarnessGenerator.Problem((string)projects.SelectedItem, (string)packages.SelectedItem, OutputFolder, projectName.Text.Trim());
                    if (problem != null) { log.Text = problem; return; }
                    DialogResult = DialogResult.OK;   // the main control generates from the session's tables
                    Close();
                    return;
                }
                busy = true;
                fields.Enabled = false;
                log.Text = "Generating the project...";
                try
                {
                    using (var template = HarnessGenerator.ResolveTemplate(settings, connectedUrl))
                        log.Text = await HarnessGenerator.Generate(manifest.Text.Trim(), template.ProjectFile, template.Package,
                                                                   OutputFolder, projectName.Text.Trim(), dontSaveSensitive.Checked);
                }
                catch (Exception ex) { log.Text = "Generation failed: " + ex.Message; }
                finally { busy = false; fields.Enabled = true; }
            };
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
        }

        /// <summary>Show the built-in template's connection fields or the reference project fields.</summary>
        private void UpdateTemplate()
        {
            var own = !builtIn.Checked;
            foreach (var field in new Control[] { sqlServer, stagingDatabase, legacyDatabase, dataverseUrl })
                SetRowVisible(field, !own);
            foreach (var field in new Control[] { reference, projects, packages })
                SetRowVisible(field, own);
            if (referenceBrowse != null) referenceBrowse.Visible = own;
            if (own && projects.Items.Count == 0) LoadProjects();   // shows a broken reference path now
            else if (!own) log.Text = "";
        }

        private void SetRowVisible(Control field, bool visible)
        {
            field.Visible = visible;
            var label = Label(field);
            if (label != field) label.Visible = visible;
        }

        private void UpdateMode()
        {
            var session = fromSession.Checked;
            manifest.Enabled = manifestBrowse.Enabled = !session;
            generate.Text = session ? "Save and Generate" : "Generate SSIS Project";
            hint.Text = session
                ? "The tables checked in the scaffolder are scripted in memory and turned into the SSIS project; no manifest or script "
                  + "files are needed. These settings are remembered, so Generate SSIS Project runs in one click next time."
                : "Generate from an earlier scaffolder run: the Scaffolder folder inside an earlier SSIS project, or files saved with "
                  + "Export > Scaffolder run. No Dataverse connection is needed.";
            hint.Text += "\nThe built-in template needs nothing but your connections; your own reference project must contain one supported "
                         + "Migrate data flow, and its connections are kept. Generation does not execute SQL or migrate data. See the results "
                         + "for skipped tables.";
        }

        private void AddTips(Button referenceBrowse, Button outputBrowse)
        {
            tip = Tips.New();
            Tips.Set(tip, "Script the tables checked in the main window in memory and build the project from them. Uses the current "
                + "Dataverse connection; no files are read.", fromSession);
            Tips.Set(tip, "Build from the files of an earlier run instead: the Scaffolder folder inside an earlier SSIS-<date-time> "
                + "project, or files saved with Export > Scaffolder run. Works without a connection and rebuilds exactly the tables "
                + "and columns of that run.", fromManifest);
            Tips.Set(tip, "Where the tables and columns come from: the live selection in the main window, or the saved files of an "
                + "earlier run.", Label(fromSession.Parent));
            Tips.Set(tip, "The manifest.json of the earlier run. Its staging and GUID scripts and meta_seed.sql must be in the same folder.\n"
                + "Defaults to the newest SSIS-*\\Scaffolder\\manifest.json in the output folder.", Label(manifest), manifest, manifestBrowse);
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
                Label(dataverseUrl), dataverseUrl);
            Tips.Set(tip, "Your hand-built SSIS solution (.sln) or project (.dtproj) to copy from. Its connection managers, project "
                + "parameters and package settings are reused. It is only read, never changed.", Label(reference), reference, referenceBrowse);
            Tips.Set(tip, "The SSIS project inside that solution. For a .dtproj reference this is the project itself.", Label(projects), projects);
            Tips.Set(tip, "The package every generated package is patterned on. It must contain one Migrate data flow with a KingswaySoft "
                + "destination. Its settings, including the checkboxes that disable plugins, workflows and auditing during the load, "
                + "are copied to every table, and so is its Stage SQL task if it has one.", Label(packages), packages);
            Tips.Set(tip, "The name of the new project: its .dtproj file and the name Visual Studio shows.", Label(projectName), projectName);
            Tips.Set(tip, "Unticked: the new project keeps the reference's protection level, and its encrypted values (such as the "
                + "Dataverse client secret) are copied unchanged. With the usual user-key setting they only open for the person who "
                + "saved the reference.\n"
                + "Ticked: the project is saved with DontSaveSensitive. Every stored password and secret is removed, so anyone can "
                + "open it and it is safe to commit. Supply them when deploying, in the SSIS catalog's connection manager settings "
                + "(e.g. CM.<connection manager>.ClientSecret) or a SQL Agent job step. To run it in Visual Studio, re-enter the "
                + "secret each time you open the project; it isn't saved.", Label(dontSaveSensitive), dontSaveSensitive);
            Tips.Set(tip, "The folder the new project is written to. It must be empty or not exist yet. Defaults to a new SSIS-<date-time> "
                + "folder in the scaffolder's output folder.", Label(output), output, outputBrowse);
            Tips.Set(tip, "Remember these settings and build the project. Nothing is executed against SQL Server or Dataverse; open and "
                + "build the result in Visual Studio.", generate);
            Tips.Set(tip, "Generation results and any errors.", log);
        }

        /// <summary>The row label next to a field added with AddRow.</summary>
        private Control Label(Control field)
        {
            return fields.GetControlFromPosition(0, fields.GetRow(field)) ?? field;
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

        private void AddRow(string label, Control control, Button browse = null)
        {
            int row = fields.RowCount++;
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(4, 5, 4, 5);
            fields.Controls.Add(control, 1, row);
            if (browse != null) fields.Controls.Add(browse, 2, row);
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
            catch (Exception ex) { if (ownTemplate.Checked) log.Text = ex.Message; }
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
            catch (Exception ex) { if (ownTemplate.Checked) log.Text = ex.Message; }
        }
    }
}
