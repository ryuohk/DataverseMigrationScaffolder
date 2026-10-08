using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DataverseMigrationScaffolder.Core;

class Program
{
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 4) throw new Exception("Usage: HarnessIntegration manifest reference.sln package.dtsx new-output");
            var projects = HarnessGenerator.Projects(args[1]);
            Assert(projects.Length > 0, "Solution project discovery");
            var project = projects.First(p => HarnessGenerator.Packages(p).Contains(args[2]));
            Assert(HarnessGenerator.Projects(project).Single() == project, "Direct project input");
            var before = File.ReadAllBytes(project);
            var packageBefore = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(project), args[2]));
            var result = HarnessGenerator.Generate(args[0], project, args[2], args[3], "IntegratedHarness").GetAwaiter().GetResult();
            Assert(File.Exists(Path.Combine(args[3], "IntegratedHarness.dtproj")), "Output project missing");
            Assert(File.Exists(Path.Combine(args[3], "harnessgen-report.json")), "Report missing");
            Assert(before.SequenceEqual(File.ReadAllBytes(project)), "Reference project changed");
            Assert(packageBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(project), args[2]))), "Reference package changed");
            foreach (var connection in Directory.GetFiles(Path.GetDirectoryName(project), "*.conmgr"))
                Assert(File.ReadAllBytes(connection).SequenceEqual(File.ReadAllBytes(Path.Combine(args[3], Path.GetFileName(connection)))), "Connection manager changed");
            bool refused = false;
            try { HarnessGenerator.Generate(args[0], project, args[2], args[3], "IntegratedHarness").GetAwaiter().GetResult(); }
            catch (ArgumentException) { refused = true; }
            Assert(refused, "Nonempty output was not refused");
            refused = false;
            try { HarnessGenerator.Generate(args[0], project, "missing.dtsx", args[3] + "-invalid", "IntegratedHarness").GetAwaiter().GetResult(); }
            catch (ArgumentException) { refused = true; }
            Assert(refused && !Directory.Exists(args[3] + "-invalid"), "Invalid package wrote output");
            // A generator error (here: an unreadable manifest) reaches the dialog as a plain message.
            var badManifest = Path.Combine(Path.GetTempPath(), "harness-integration-bad-manifest.json");
            File.WriteAllText(badManifest, "{ not json");
            string message = null;
            try { HarnessGenerator.Generate(badManifest, project, args[2], args[3] + "-bad", "IntegratedHarness").GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { message = ex.Message; }
            finally { File.Delete(badManifest); }
            Assert(message != null && message.StartsWith("Scaffolder manifest is not valid JSON"), "Generator errors are reported: " + message);
            Assert(!Directory.Exists(args[3] + "-bad"), "Failed generation wrote output");
            // In memory (the Generate SSIS Project button): the same scaffolder outputs as text must give
            // the same project; only the output folder written into Stage file connections differs.
            var dir = Path.GetDirectoryName(Path.GetFullPath(args[0]));
            var files = Directory.GetFiles(dir).Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "manifest.json")
                .Select(f => new GeneratedFile { FileName = Path.GetFileName(f), Content = File.ReadAllText(f) }).ToList();
            var memoryOut = args[3] + "-memory";
            HarnessGenerator.GenerateFromRun(files, project, args[2], memoryOut, "IntegratedHarness");
            var fileOutput = Path.GetFullPath(args[3]);
            foreach (var path in Directory.GetFiles(fileOutput, "*", SearchOption.AllDirectories))
            {
                var relative = path.Substring(fileOutput.Length + 1);
                var expected = File.ReadAllText(path).Replace(fileOutput, Path.GetFullPath(memoryOut));
                Assert(File.Exists(Path.Combine(memoryOut, relative)) && File.ReadAllText(Path.Combine(memoryOut, relative)) == expected,
                       "In-memory output differs: " + relative);
            }
            Assert(Directory.GetFiles(memoryOut, "*", SearchOption.AllDirectories).Length == Directory.GetFiles(fileOutput, "*", SearchOption.AllDirectories).Length,
                   "In-memory output has a different file count");
            // SSIS Settings > "Don't save passwords or secrets": the project and every package say DontSaveSensitive.
            var clearedOut = args[3] + "-dontsave";
            var summary = HarnessGenerator.GenerateFromRun(files, project, args[2], clearedOut, "IntegratedHarness", dontSaveSensitive: true);
            Assert(File.ReadAllText(Path.Combine(clearedOut, "IntegratedHarness.dtproj")).Contains("SSIS:ProtectionLevel=\"DontSaveSensitive\""),
                   "DontSaveSensitive not applied to the project");
            Assert(Directory.GetFiles(clearedOut, "*.dtsx").All(f => File.ReadAllText(f).Contains("DTS:ProtectionLevel=\"0\"")),
                   "DontSaveSensitive not applied to every package");
            Assert(summary.Contains("protection: DontSaveSensitive"), "Summary does not mention the protection level");
            // The built-in template (scaffolder 1.2026.10.8+ staging scripts carry tablename, which it logs).
            var staging = new Regex(@"(CREATE TABLE \[\w+\]\.\[(stage_\w+)\]\((?:(?!\n\s*\);).)*?\[statecode\] INT)(,?)", RegexOptions.Singleline);
            var named = files.Select(f => new GeneratedFile { FileName = f.FileName, Content = f.FileName.EndsWith("_create_staging.sql")
                ? staging.Replace(f.Content, m => m.Groups[1].Value + ",\n    [tablename] NVARCHAR(100) DEFAULT '" + m.Groups[2].Value + "'" + m.Groups[3].Value)
                : f.Content }).ToList();
            var builtInOut = args[3] + "-builtin";
            var settings = new ToolSettings { HarnessTemplate = ToolSettings.BuiltInTemplate, HarnessSqlServer = @"SQL01\INST",
                                              HarnessStagingDatabase = "Stage_DB", HarnessLegacyDatabase = "Old_DB" };
            Assert(settings.UsesBuiltInTemplate && HarnessGenerator.SettingsComplete(settings), "Built-in settings complete");
            string unpacked;
            using (var template = HarnessGenerator.ResolveTemplate(settings, "https://contoso.crm.dynamics.com/"))
            {
                unpacked = Path.GetDirectoryName(Path.GetDirectoryName(template.ProjectFile));
                Assert(File.Exists(template.ProjectFile) && template.Package == "01b - Harness.dtsx", "Built-in template unpacked");
                HarnessGenerator.GenerateFromRun(named, template.ProjectFile, template.Package, builtInOut, "BuiltInHarness");
            }
            Assert(!Directory.Exists(unpacked), "Built-in template's temporary copy removed");
            Func<string, string> read = name => File.ReadAllText(Path.Combine(builtInOut, name));
            Assert(read("Staging.conmgr").Contains(@"Data Source=SQL01\INST;Initial Catalog=Stage_DB;"), "Built-in staging connection");
            Assert(read("Legacy.conmgr").Contains(@"Data Source=SQL01\INST;Initial Catalog=Old_DB;"), "Built-in legacy connection");
            Assert(read("Dynamics CRM Connection Manager.conmgr").Contains(";ServerUrl=https://contoso.crm.dynamics.com;"), "Built-in Dataverse URL");
            Assert(Regex.IsMatch(read("BuiltInHarness.dtproj"), @"CM\.Staging\.ServerName"">(?:(?!</SSIS:Parameter>).)*?Name=""Value"">SQL01\\INST<", RegexOptions.Singleline),
                   "Built-in project parameters");
            var stageSql = Directory.GetFiles(Path.Combine(builtInOut, "Queries"), "*.sql").Select(File.ReadAllText).ToList();
            Assert(stageSql.Count > 0 && stageSql.All(t => t.Contains("FROM [Old_DB].[dbo].")), "Built-in stage SQL reads the legacy database");
            // Only the report names the template's sample table (as the reference it was built from).
            Assert(!Directory.EnumerateFiles(builtInOut, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("harnessgen-report.json"))
                             .Any(f => Regex.IsMatch(File.ReadAllText(f), "new_category", RegexOptions.IgnoreCase)), "No sample table names left");
            Console.WriteLine(result);
            Console.WriteLine("PASS: in-process generation, solution/project discovery, reference preservation, connection preservation, output safety, invalid package rejection, error reporting, DontSaveSensitive and the built-in template.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
