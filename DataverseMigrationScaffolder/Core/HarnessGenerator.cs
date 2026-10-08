using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using DataverseMigrationScaffolder.HarnessGen;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>Generates the SSIS migration harness project (DataverseMigrationScaffolder.HarnessGen).</summary>
    public static class HarnessGenerator
    {
        public static string[] Projects(string input)
        {
            input = Path.GetFullPath(input);
            if (!File.Exists(input)) throw new ArgumentException("Reference file not found.");
            if (string.Equals(Path.GetExtension(input), ".dtproj", StringComparison.OrdinalIgnoreCase))
                return new[] { input };
            if (!string.Equals(Path.GetExtension(input), ".sln", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an SSIS .dtproj or .sln file.");
            var paths = new List<string>();
            foreach (var line in File.ReadLines(input))
            {
                var match = Regex.Match(line, "^Project\\([^)]*\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+\\.dtproj)\"", RegexOptions.IgnoreCase);
                if (match.Success) paths.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(input), match.Groups[1].Value)));
            }
            if (paths.Count == 0) throw new ArgumentException("This solution contains no SSIS projects.");
            return paths.ToArray();
        }

        public static string[] Packages(string project)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(project, settings))
            {
                XNamespace ns = "www.microsoft.com/SqlServer/SSIS";
                return XDocument.Load(reader).Descendants(ns + "Packages").Elements(ns + "Package")
                    .Select(p => (string)p.Attribute(ns + "Name")).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            }
        }

        /// <summary>Why these reference choices cannot be used, or null.</summary>
        public static string Problem(string project, string package, string output, string name)
        {
            if (string.IsNullOrWhiteSpace(project) || !File.Exists(project)) return "Choose a valid reference SSIS project first.";
            string[] packages;
            try { packages = Packages(project); }
            catch (Exception ex) { return "The reference project cannot be read: " + ex.Message; }
            if (!packages.Contains(package)) return "Select a package listed in the reference project.";
            if (string.IsNullOrWhiteSpace(output)) return "Choose a new output folder.";
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                return "Choose a new, empty output folder. Existing output is not replaced by this integration.";
            if (string.IsNullOrWhiteSpace(name)) return "Enter a name for the new SSIS project.";
            return null;
        }

        /// <summary>Why the built-in template's connection settings cannot be used, or null.</summary>
        public static string BuiltInProblem(string server, string stagingDatabase, string legacyDatabase, string output, string name)
        {
            if (string.IsNullOrWhiteSpace(server)) return "Enter the SQL Server that holds the staging and legacy databases.";
            if (string.IsNullOrWhiteSpace(stagingDatabase)) return "Enter the name of the staging database.";
            if (string.IsNullOrWhiteSpace(legacyDatabase)) return "Enter the name of the legacy database.";
            if (string.IsNullOrWhiteSpace(output)) return "Choose a new output folder.";
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                return "Choose a new, empty output folder. Existing output is not replaced by this integration.";
            if (string.IsNullOrWhiteSpace(name)) return "Enter a name for the new SSIS project.";
            return null;
        }

        /// <summary>True when the remembered SSIS settings can be used without asking. The template choice
        /// must have been made once in SSIS Settings.</summary>
        public static bool SettingsComplete(ToolSettings settings)
        {
            if (string.IsNullOrEmpty(settings.HarnessTemplate)) return false;
            return settings.UsesBuiltInTemplate
                ? BuiltInProblem(settings.HarnessSqlServer, settings.HarnessStagingDatabase, settings.HarnessLegacyDatabase, "x",
                                 settings.HarnessProjectName) == null
                : Problem(settings.HarnessProjectFile, settings.HarnessPackage, "x", settings.HarnessProjectName) == null;
        }

        // ---- built-in template ------------------------------------------------------------------

        private const string BuiltInResources = "BuiltInTemplate/";
        public const string BuiltInProjectFile = "MigrationHarnessTemplate/MigrationHarnessTemplate.dtproj";
        public const string BuiltInPackage = "01b - Harness.dtsx";
        /// <summary>The Dataverse URL the built-in template uses when none is known.</summary>
        public const string PlaceholderDataverseUrl = "https://yourorg.crm.dynamics.com";

        /// <summary>The template to generate from: the remembered reference project, or the built-in
        /// template unpacked with the remembered connection settings (deleted on Dispose).</summary>
        public static HarnessTemplate ResolveTemplate(ToolSettings settings, string connectedUrl)
        {
            if (!settings.UsesBuiltInTemplate) return new HarnessTemplate(settings.HarnessProjectFile, settings.HarnessPackage, null);
            var url = !string.IsNullOrWhiteSpace(settings.HarnessDataverseUrl) ? settings.HarnessDataverseUrl
                      : !string.IsNullOrWhiteSpace(connectedUrl) ? connectedUrl : PlaceholderDataverseUrl;
            return UnpackBuiltIn(settings.HarnessSqlServer, settings.HarnessStagingDatabase, settings.HarnessLegacyDatabase, url);
        }

        /// <summary>Unpack the built-in template to a new temporary folder, pointed at these connections.</summary>
        public static HarnessTemplate UnpackBuiltIn(string server, string stagingDatabase, string legacyDatabase, string dataverseUrl)
        {
            var problem = BuiltInProblem(server, stagingDatabase, legacyDatabase, "x", "x");
            if (problem != null) throw new ArgumentException(problem);
            server = server.Trim();
            stagingDatabase = stagingDatabase.Trim();
            legacyDatabase = legacyDatabase.Trim();
            dataverseUrl = (dataverseUrl ?? "").Trim().TrimEnd('/');
            if (dataverseUrl.Length == 0) dataverseUrl = PlaceholderDataverseUrl;

            var root = Path.Combine(Path.GetTempPath(), "DataverseMigrationScaffolder", "template-" + Guid.NewGuid().ToString("N"));
            var assembly = typeof(HarnessGenerator).Assembly;
            try
            {
                foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(BuiltInResources, StringComparison.Ordinal)))
                {
                    var path = Path.Combine(root, resource.Substring(BuiltInResources.Length).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var source = assembly.GetManifestResourceStream(resource))
                    using (var target = File.Create(path))
                        source.CopyTo(target);
                }
                var project = Path.Combine(root, BuiltInProjectFile.Replace('/', Path.DirectorySeparatorChar));
                var folder = Path.GetDirectoryName(project);
                if (!File.Exists(project)) throw new InvalidOperationException("The built-in template is missing from this build of the plugin.");

                var stagingConnection = Xml(SqlConnection(server, stagingDatabase));
                var legacyConnection = Xml(SqlConnection(server, legacyDatabase));
                Rewrite(Path.Combine(folder, "Staging.conmgr"), t => Replace(t, "Data Source=localhost;Initial Catalog=Staging", stagingConnection));
                Rewrite(Path.Combine(folder, "Legacy.conmgr"), t => Replace(t, "Data Source=localhost;Initial Catalog=Legacy", legacyConnection));
                Rewrite(Path.Combine(folder, "Dynamics CRM Connection Manager.conmgr"),
                        t => Replace(t, ";ServerUrl=" + PlaceholderDataverseUrl + ";", ";ServerUrl=" + Xml(dataverseUrl) + ";"));
                Rewrite(project, t =>
                {
                    // The project file's copies of the connection settings (used when deploying).
                    t = Replace(t, "Data Source=localhost;Initial Catalog=Staging", stagingConnection);
                    t = Replace(t, "Data Source=localhost;Initial Catalog=Legacy", legacyConnection);
                    t = Replace(t, ";ServerUrl=" + PlaceholderDataverseUrl + ";", ";ServerUrl=" + Xml(dataverseUrl) + ";");
                    t = ParameterValue(t, "CM.Staging.ServerName", Xml(server));
                    t = ParameterValue(t, "CM.Staging.InitialCatalog", Xml(stagingDatabase));
                    t = ParameterValue(t, "CM.Legacy.ServerName", Xml(server));
                    t = ParameterValue(t, "CM.Legacy.InitialCatalog", Xml(legacyDatabase));
                    return ParameterValue(t, "CM.Dynamics CRM Connection Manager.ServerUrl", Xml(dataverseUrl));
                });
                // The staging SQL reads the legacy tables by three-part name.
                foreach (var sql in Directory.GetFiles(Path.Combine(root, "Queries"), "*.sql"))
                    Rewrite(sql, t => Replace(t, "[Legacy].[dbo].", "[" + legacyDatabase.Replace("]", "]]") + "].[dbo]."));
                return new HarnessTemplate(project, BuiltInPackage, root);
            }
            catch
            {
                TryDelete(root);
                throw;
            }
        }

        private static string SqlConnection(string server, string database)
        {
            return "Data Source=" + server + ";Initial Catalog=" + database;
        }

        private static string Xml(string value)
        {
            return System.Security.SecurityElement.Escape(value);
        }

        /// <summary>Replace every occurrence; the template must contain the text.</summary>
        private static string Replace(string text, string old, string value)
        {
            if (!text.Contains(old)) throw new InvalidOperationException("The built-in template does not contain " + old + ".");
            return text.Replace(old, value);
        }

        /// <summary>Set the Value of a connection manager parameter (CM.&lt;manager&gt;.&lt;property&gt;) in a .dtproj.</summary>
        private static string ParameterValue(string text, string parameter, string value)
        {
            var pattern = "(<SSIS:Parameter\\s+SSIS:Name=\"" + Regex.Escape(parameter) + "\">(?:(?!</SSIS:Parameter>).)*?"
                          + "<SSIS:Property SSIS:Name=\"Value\">)[^<]*";
            var regex = new Regex(pattern, RegexOptions.Singleline);
            if (!regex.IsMatch(text)) throw new InvalidOperationException("The built-in template has no " + parameter + " parameter.");
            return regex.Replace(text, m => m.Groups[1].Value + value);
        }

        private static void Rewrite(string path, Func<string, string> change)
        {
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = new System.Text.UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            File.WriteAllBytes(path, (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : new byte[0]).Concat(new System.Text.UTF8Encoding(false).GetBytes(change(text))).ToArray());
        }

        internal static void TryDelete(string folder)
        {
            try { if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Subfolder of each generated SSIS project holding the scaffolder run it was built from.</summary>
        public const string RunFolder = "Scaffolder";

        /// <summary>The manifest.json of the most recent SSIS project in the output folder (its Scaffolder
        /// subfolder), else output\manifest.json (an exported scaffolder run).</summary>
        public static string LatestRunManifest(string scaffolderFolder)
        {
            if (string.IsNullOrWhiteSpace(scaffolderFolder)) return "";
            try
            {
                var latest = Directory.Exists(scaffolderFolder)
                    ? Directory.GetDirectories(scaffolderFolder, "SSIS-*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                               .Select(d => Path.Combine(d, RunFolder, "manifest.json")).FirstOrDefault(File.Exists)
                    : null;
                return latest ?? Path.Combine(scaffolderFolder, "manifest.json");
            }
            catch (IOException) { return Path.Combine(scaffolderFolder, "manifest.json"); }
            catch (UnauthorizedAccessException) { return Path.Combine(scaffolderFolder, "manifest.json"); }
        }

        /// <summary>A new output folder for an SSIS project: SSIS-yyyyMMdd-HHmmss under the scaffolder's output folder.</summary>
        public static string NewOutputFolder(string scaffolderFolder)
        {
            return Path.Combine(scaffolderFolder, "SSIS-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        private static GenerationOptions Options(string project, string package, string output, string name, bool dontSaveSensitive)
        {
            var problem = Problem(project, package, output, name);
            if (problem != null) throw new ArgumentException(problem);
            return new GenerationOptions
            {
                DtprojPath = Path.GetFullPath(project),
                ReferencePackage = package,
                OutputDir = Path.GetFullPath(output),
                ProjectName = name,
                Protection = dontSaveSensitive ? Support.DontSaveSensitive : HarnessProject.ProtectionReference,
                Generator = "Dataverse Migration Scaffolder " + typeof(HarnessGenerator).Assembly.GetName().Version,
            };
        }

        private static string Run(GenerationOptions options)
        {
            try { return string.Join(Environment.NewLine, HarnessProject.Generate(options).SummaryLines()); }
            catch (GeneratorException ex) { throw new InvalidOperationException(ex.Message, ex); }
        }

        /// <summary>Generate the SSIS project from a previous scaffolder run (manifest.json and the scripts
        /// next to it); returns the completion summary.</summary>
        public static Task<string> Generate(string manifest, string project, string package, string output, string name,
                                            bool dontSaveSensitive = false)
        {
            if (!File.Exists(manifest)) throw new ArgumentException("Select the scaffolder manifest.json first.");
            var options = Options(project, package, output, name, dontSaveSensitive);
            options.ManifestPath = Path.GetFullPath(manifest);
            // Off the UI thread: a large manifest takes a few seconds.
            return Task.Run(() => Run(options));
        }

        /// <summary>Generate the SSIS project from a scaffolder run held in memory (the manifest, staging and
        /// GUID scripts and meta seed produced with ToolSettings.ForHarness); returns the completion
        /// summary. Runs on the calling thread.</summary>
        public static string GenerateFromRun(IEnumerable<GeneratedFile> files, string project, string package, string output, string name,
                                             bool dontSaveSensitive = false)
        {
            var options = Options(project, package, output, name, dontSaveSensitive);
            var list = files.ToList();
            var manifest = list.FirstOrDefault(f => f.FileName == "manifest.json");
            if (manifest == null) throw new InvalidOperationException("The scaffolder run produced no manifest.json.");
            options.ManifestText = manifest.Content;
            options.ManifestName = "manifest.json";
            options.Scripts = list.Where(f => f.BinaryContent == null && f.FileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                                  .ToDictionary(f => f.FileName, f => f.Content, StringComparer.Ordinal);
            var seed = list.FirstOrDefault(f => f.FileName == "meta_seed.sql");
            options.MetaSeedText = seed != null ? seed.Content : null;
            return Run(options);
        }
    }

    /// <summary>A reference project to generate from. The built-in template lives in a temporary
    /// folder that Dispose removes; a user's own reference project is never touched.</summary>
    public sealed class HarnessTemplate : IDisposable
    {
        private readonly string temporaryRoot;

        public string ProjectFile { get; private set; }
        public string Package { get; private set; }

        internal HarnessTemplate(string projectFile, string package, string temporaryRoot)
        {
            ProjectFile = projectFile;
            Package = package;
            this.temporaryRoot = temporaryRoot;
        }

        public void Dispose()
        {
            HarnessGenerator.TryDelete(temporaryRoot);
        }
    }
}
