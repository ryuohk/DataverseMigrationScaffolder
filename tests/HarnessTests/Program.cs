using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DataverseMigrationScaffolder.HarnessGen;

/// <summary>
/// Generator regression tests. Usage: HarnessTests [TestData folder] [--update]
///
/// Each scenario in TestData/scenarios.json is generated twice, from files and from text held in
/// memory (as the plugin does), and compared byte for byte with TestData/Expected/&lt;scenario&gt;,
/// where the absolute output folder is written as {OUTPUT}. Error scenarios must fail with the
/// recorded message. --update rewrites the expected output after an intended change.
/// </summary>
internal static class Program
{
    private const string Placeholder = "{OUTPUT}";

    private static int Main(string[] args)
    {
        var update = args.Contains("--update");
        var data = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindTestData();
        if (data == null || !File.Exists(Path.Combine(data, "scenarios.json")))
        {
            Console.Error.WriteLine("TestData folder not found; pass it as the first argument.");
            return 2;
        }
        var scenarios = (List<object>)Json.Parse(File.ReadAllText(Path.Combine(data, "scenarios.json")));
        var root = Path.Combine(Path.GetTempPath(), "HarnessTests-" + Guid.NewGuid().ToString("N"));
        var failed = 0;
        try
        {
            foreach (JObj scenario in scenarios)
            {
                var name = (string)scenario.Get("name");
                var args0 = ((List<object>)scenario.Get("args")).Cast<string>()
                    .Select(a => a.StartsWith("Fixtures/") ? Path.Combine(data, a.Replace('/', Path.DirectorySeparatorChar)) : a).ToList();
                var error = scenario.Get("error") as string;
                var errorContains = scenario.Get("errorContains") as string;
                if (error != null || errorContains != null)
                {
                    var message = Fail(args0, Path.Combine(root, name));
                    var same = message != null && (error != null ? message == error : message.Contains(errorContains));
                    Console.WriteLine("{0}: {1}", name, same ? "SAME ERROR" : "DIFFERENT");
                    if (!same) { Console.WriteLine("    expected: {0}\n    actual:   {1}", error ?? "..." + errorContains + "...", message ?? "(no error)"); failed++; }
                    continue;
                }
                var expected = Path.Combine(data, "Expected", name);
                foreach (var inMemory in new[] { false, true })
                {
                    var output = Path.Combine(root, name + (inMemory ? "-memory" : ""));
                    string problem;
                    try
                    {
                        HarnessProject.Generate(Options(args0, output, inMemory));
                        Normalize(output);
                        if (update && !inMemory)
                        {
                            if (Directory.Exists(expected)) Directory.Delete(expected, true);
                            CopyFolder(output, expected);
                        }
                        problem = Compare(expected, output);
                    }
                    catch (GeneratorException ex) { problem = "generation failed: " + ex.Message; }
                    Console.WriteLine("{0} [{1}]: {2}", name, inMemory ? "in-memory" : "files", problem == null ? "IDENTICAL" : "DIFFERENT");
                    if (problem != null) { Console.WriteLine("    " + problem); failed++; }
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(failed == 0 ? "PASS" : "FAIL: " + failed + " check(s)");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>TestData next to the project (the executable runs from bin\Release\net48).</summary>
    private static string FindTestData()
    {
        for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestData", "scenarios.json"))) return Path.Combine(dir.FullName, "TestData");
        return null;
    }

    /// <summary>The generator options for the original command-line arguments.</summary>
    private static GenerationOptions Options(List<string> args, string output, bool inMemory)
    {
        var options = new GenerationOptions { OutputDir = output };
        for (var i = 0; i < args.Count; i++)
        {
            Func<string> next = () => args[++i];
            switch (args[i])
            {
                case "--manifest": options.ManifestPath = next(); break;
                case "--reference-dtproj": options.DtprojPath = next(); break;
                case "--reference-package": options.ReferencePackage = next(); break;
                case "--reference-table": options.ReferenceTable = next(); break;
                case "--tables": options.Tables = next().Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList(); break;
                case "--keep-reference": options.KeepReference = true; break;
                case "--project-name": options.ProjectName = next(); break;
                case "--meta-seed": options.MetaSeed = next(); break;
                case "--allow-missing-dependencies": options.AllowMissingDependencies = true; break;
                case "--protection": options.Protection = next(); break;
                default: throw new ArgumentException("unrecognized argument: " + args[i]);
            }
        }
        if (inMemory)
        {
            // What the plugin does: hand over the scaffolder outputs as text, not files.
            var dir = Path.GetDirectoryName(Path.GetFullPath(options.ManifestPath));
            options.ManifestText = File.ReadAllText(options.ManifestPath);
            options.ManifestName = Path.GetFileName(options.ManifestPath);
            options.Scripts = Directory.GetFiles(dir, "*_create_*.sql").ToDictionary(Path.GetFileName, File.ReadAllText);
            var seed = options.MetaSeed ?? Path.Combine(dir, "meta_seed.sql");
            if (File.Exists(seed)) options.MetaSeedText = File.ReadAllText(seed);
            options.ManifestPath = null;
            options.MetaSeed = null;
        }
        return options;
    }

    /// <summary>The generator's error message, or null when generation succeeded.</summary>
    private static string Fail(List<string> args, string output)
    {
        try { HarnessProject.Generate(Options(args, output, false)); return null; }
        catch (GeneratorException ex) { return ex.Message; }
    }

    /// <summary>Write the absolute output folder as {OUTPUT}, as written in XML and in JSON.</summary>
    private static void Normalize(string output)
    {
        var full = Path.GetFullPath(output);
        var plain = Encoding.UTF8.GetBytes(full);
        var escaped = Encoding.UTF8.GetBytes(full.Replace("\\", "\\\\"));
        var mark = Encoding.UTF8.GetBytes(Placeholder);
        foreach (var path in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
        {
            var data = File.ReadAllBytes(path);
            var changed = Replace(Replace(data, escaped, mark), plain, mark);
            if (!changed.SequenceEqual(data)) File.WriteAllBytes(path, changed);
        }
    }

    private static byte[] Replace(byte[] data, byte[] old, byte[] value)
    {
        var result = new List<byte>(data.Length);
        for (var i = 0; i < data.Length;)
        {
            if (i <= data.Length - old.Length && Matches(data, i, old)) { result.AddRange(value); i += old.Length; }
            else result.Add(data[i++]);
        }
        return result.ToArray();
    }

    private static bool Matches(byte[] data, int at, byte[] pattern)
    {
        for (var j = 0; j < pattern.Length; j++) if (data[at + j] != pattern[j]) return false;
        return true;
    }

    /// <summary>The first difference between the expected and the actual folder, or null.</summary>
    private static string Compare(string expected, string actual)
    {
        if (!Directory.Exists(expected)) return "no expected output (run with --update to record it)";
        Func<string, List<string>> files = root => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => f.Substring(root.Length + 1)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var a = files(expected);
        var b = files(actual);
        var onlyExpected = a.Except(b).ToList();
        var onlyActual = b.Except(a).ToList();
        if (onlyExpected.Count > 0) return "missing: " + string.Join(", ", onlyExpected);
        if (onlyActual.Count > 0) return "unexpected: " + string.Join(", ", onlyActual);
        foreach (var file in a)
        {
            var x = File.ReadAllBytes(Path.Combine(expected, file));
            var y = File.ReadAllBytes(Path.Combine(actual, file));
            if (x.SequenceEqual(y)) continue;
            var i = 0;
            while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
            Func<byte[], string> around = d => Encoding.UTF8.GetString(d, Math.Max(0, i - 50), Math.Min(d.Length, i + 50) - Math.Max(0, i - 50));
            return string.Format("differs: {0} at byte {1}\n      expected: {2}\n      actual:   {3}", file, i,
                                 around(x).Replace("\r", "\\r").Replace("\n", "\\n"), around(y).Replace("\r", "\\r").Replace("\n", "\\n"));
        }
        return null;
    }

    private static void CopyFolder(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, file.Substring(source.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(file, dest);
        }
    }
}
