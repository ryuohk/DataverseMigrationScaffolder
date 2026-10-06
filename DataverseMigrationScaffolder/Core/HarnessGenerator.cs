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

        /// <summary>True when the remembered SSIS settings can be used without asking.</summary>
        public static bool SettingsComplete(ToolSettings settings)
        {
            return Problem(settings.HarnessProjectFile, settings.HarnessPackage, "x", settings.HarnessProjectName) == null;
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
}
