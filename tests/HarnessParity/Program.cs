using System;
using System.Collections.Generic;
using System.Linq;
using DataverseMigrationScaffolder.HarnessGen;

/// <summary>harnessgen with the original Python tool's arguments (for output comparisons).</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = new GenerationOptions();
        var inMemory = false;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                Func<string> next = () =>
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("argument " + args[i] + ": expected one argument");
                    return args[++i];
                };
                switch (args[i])
                {
                    case "--manifest": options.ManifestPath = next(); break;
                    case "--reference-dtproj": options.DtprojPath = next(); break;
                    case "--output": options.OutputDir = next(); break;
                    case "--reference-package": options.ReferencePackage = next(); break;
                    case "--reference-table": options.ReferenceTable = next(); break;
                    case "--tables": options.Tables = next().Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList(); break;
                    case "--keep-reference": options.KeepReference = true; break;
                    case "--project-name": options.ProjectName = next(); break;
                    case "--meta-seed": options.MetaSeed = next(); break;
                    case "--allow-missing-dependencies": options.AllowMissingDependencies = true; break;
                    case "--in-memory": inMemory = true; break;
                    case "--protection": options.Protection = next(); break;
                    default: throw new ArgumentException("unrecognized arguments: " + args[i]);
                }
            }
            var missing = new List<string>();
            if (options.ManifestPath == null) missing.Add("--manifest");
            if (options.DtprojPath == null) missing.Add("--reference-dtproj");
            if (options.OutputDir == null) missing.Add("--output");
            if (missing.Count > 0) throw new ArgumentException("the following arguments are required: " + string.Join(", ", missing));
            if (inMemory)
            {
                // What the plugin does: hand over the scaffolder outputs as text, not files.
                var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(options.ManifestPath));
                options.ManifestText = System.IO.File.ReadAllText(options.ManifestPath);
                options.ManifestName = System.IO.Path.GetFileName(options.ManifestPath);
                options.Scripts = System.IO.Directory.GetFiles(dir, "*_create_*.sql")
                    .ToDictionary(System.IO.Path.GetFileName, System.IO.File.ReadAllText);
                var seed = options.MetaSeed ?? System.IO.Path.Combine(dir, "meta_seed.sql");
                if (System.IO.File.Exists(seed)) options.MetaSeedText = System.IO.File.ReadAllText(seed);
                options.ManifestPath = null;
                options.MetaSeed = null;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("harnessgen: error: " + ex.Message);
            return 2;
        }
        try
        {
            var result = HarnessProject.Generate(options);
            foreach (var line in result.SummaryLines()) Console.WriteLine(line);
            return 0;
        }
        catch (GeneratorException ex)
        {
            Console.Error.WriteLine("harnessgen: error: " + ex.Message);
            return 2;
        }
    }
}
