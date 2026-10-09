using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DataverseMigrationScaffolder.HarnessGen;

/// <summary>
/// Make the plugin's built-in template from a reference harness project.
///
/// Usage: MakeBuiltInTemplate &lt;reference repo&gt; &lt;output folder&gt; &lt;manifest.json&gt; &lt;rules.json&gt;
///
/// The built-in template ships publicly inside the plugin, so this copies the committed files of a
/// reference project (a git clone) and removes everything specific to an organization, environment
/// or person:
/// - the renames in rules.json give the sample table and its columns neutral names (the generator
///   replaces them with each migrated table's real names anyway);
/// - connection managers point at localhost and a placeholder Dataverse URL, with no client id;
/// - stored secrets and the project's password verifier are removed;
/// - creator and machine names are cleared, and file paths are made neutral.
/// It also writes template.json (the sample table's primary name and match key, from the manifest,
/// which must contain the reference's table), then fails if anything specific is left: the
/// "forbidden" patterns of rules.json plus personal paths, secrets, client ids and org URLs.
///
/// rules.json: { "renames": [["old", "new"], ...], "forbidden": ["regex", ...] }. It holds
/// organization-specific names, so keep it out of this public repository.
/// </summary>
internal static class Program
{
    private const string Server = "localhost";
    private const string DataverseUrl = "https://yourorg.crm.dynamics.com";
    private const string NoClientId = "00000000-0000-0000-0000-000000000000";
    private static readonly string[] Generic =
    {
        @"(?i)c:\\users\\", "AQAAANCMnd8", @"(?i)azuread", @"(?i)(?<!yourorg)\.crm\d*\.dynamics\.com",
        @"(?i)ClientAppId=(?!" + NoClientId + ")[0-9a-f-]{36}",
    };

    private static List<KeyValuePair<string, string>> renames;

    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: MakeBuiltInTemplate <reference repo> <output folder> <manifest.json> <rules.json>");
            return 2;
        }
        string repo = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        var rules = (JObj)Json.Parse(File.ReadAllText(args[3]));
        renames = ((List<object>)rules.Get("renames")).Cast<List<object>>()
            .Select(p => new KeyValuePair<string, string>((string)p[0], (string)p[1])).ToList();
        var forbidden = Generic.Concat(((List<object>)rules.Get("forbidden") ?? new List<object>()).Cast<string>()).ToList();

        var files = CommittedFiles(repo);
        var legacy = files.First(f => Path.GetFileName(f) == "Legacy.conmgr");
        var legacyDoc = new XmlDocument();
        legacyDoc.Load(legacy);
        var legacyManager = legacyDoc.DocumentElement.GetAttribute("DTS:ObjectName");
        var hint = ReferenceHint(files, args[2]);

        if (Directory.Exists(output)) Directory.Delete(output, true);
        foreach (var source in files)
        {
            var relative = source.Substring(repo.Length + 1);
            var target = Path.Combine(output, string.Join(Path.DirectorySeparatorChar.ToString(),
                                                          relative.Split(Path.DirectorySeparatorChar).Select(p => Neutral(p, legacyManager))));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var raw = File.ReadAllBytes(source);
            var bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            var text = new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
            var bytes = new UTF8Encoding(false).GetBytes(Neutral(text, legacyManager));
            File.WriteAllBytes(target, bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes);
        }
        var project = Path.GetDirectoryName(Directory.GetFiles(output, "*.dtproj", SearchOption.AllDirectories).First());
        File.WriteAllText(Path.Combine(project, "template.json"),
            ("{\n  \"table\": \"" + Neutral(hint[0], legacyManager) + "\",\n  \"primaryName\": "
            + (hint[1] == null ? "null" : "\"" + Neutral(hint[1], legacyManager) + "\"") + ",\n  \"matchKey\": \""
            + Neutral(hint[2], legacyManager) + "\"\n}\n").Replace("\n", "\r\n"), new UTF8Encoding(false));

        var problems = new List<string>();
        foreach (var path in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            foreach (var pattern in forbidden)
                foreach (Match m in Regex.Matches(text, pattern))
                    problems.Add(Path.GetFileName(path) + ": " + text.Substring(Math.Max(0, m.Index - 40), Math.Min(text.Length, m.Index + m.Length + 40) - Math.Max(0, m.Index - 40)));
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dtsx" || ext == ".dtproj" || ext == ".conmgr" || ext == ".params" || ext == ".database")
                new XmlDocument().Load(path);   // still well-formed XML
        }
        foreach (var p in problems.Take(20)) Console.WriteLine("LEFT: " + p.Replace("\r", " ").Replace("\n", " "));
        Console.WriteLine("{0} files written to {1}; {2} specific token(s) left",
                          Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length, output, problems.Count);
        return problems.Count == 0 ? 0 : 1;
    }

    private static List<string> CommittedFiles(string repo)
    {
        var info = new ProcessStartInfo("git", "-C \"" + repo + "\" ls-files")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        string listing;
        using (var git = Process.Start(info))
        {
            listing = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            if (git.ExitCode != 0) throw new InvalidOperationException("git ls-files failed in " + repo);
        }
        var keep = new[] { ".dtproj", ".database", ".params", ".conmgr", ".dtsx", ".sql" };
        return listing.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
            .Where(l => keep.Any(k => l.EndsWith(k, StringComparison.OrdinalIgnoreCase)))
            .Select(l => Path.Combine(repo, l.Replace('/', Path.DirectorySeparatorChar))).ToList();
    }

    /// <summary>[table, primary name, match key] of the reference's table, from the manifest.</summary>
    private static string[] ReferenceHint(List<string> files, string manifestPath)
    {
        var package = files.First(f => f.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase));
        var manifest = Metadata.LoadManifest(manifestPath);
        var flow = Stage.SplitReference(File.ReadAllBytes(package), Path.GetFileName(package), Path.GetDirectoryName(package)).Item1;
        var reference = Reference.Infer(flow, Path.GetFileName(package), manifest.StagingPrefix).Item1;
        var table = manifest.Table(reference.LogicalName);
        if (table == null) throw new InvalidOperationException(manifestPath + " does not contain the reference table " + reference.LogicalName);
        reference = Reference.Infer(flow, Path.GetFileName(package), manifest.StagingPrefix, table).Item1;
        return new[] { reference.LogicalName, reference.PrimaryName, reference.MatchKeys[0] };
    }

    private static string Neutral(string text, string legacyManager)
    {
        foreach (var pair in renames) text = text.Replace(pair.Key, pair.Value);
        // The Legacy connection manager is named after its server; give it a plain name.
        text = text.Replace(legacyManager, "Legacy");
        text = Regex.Replace(text, "Data Source=[^;\"<]*", "Data Source=" + Server);
        text = Regex.Replace(text, "Application Name=[^;\"<]*", "Application Name=SSIS-MigrationHarness");
        // Only the connection's own ServerUrl (AuthorizationServerUrl stays as it is).
        text = Regex.Replace(text, "(?<![A-Za-z])ServerUrl=[^;\"<]*", "ServerUrl=" + DataverseUrl);
        text = ParameterValue(text, "ServerUrl", DataverseUrl);
        text = ParameterValue(text, "ServerName", Server);
        text = Regex.Replace(text, "ClientAppId=[^;\"<]*", "ClientAppId=" + NoClientId);
        text = ParameterValue(text, "ClientAppId", NoClientId);
        // Stored secrets (KingswaySoft ClientSecret, the project's PasswordVerifier).
        text = Regex.Replace(text, "(Sensitive=\"1\"[^>]*>)[^<]+(<)", "$1$2");
        // Creator and machine names.
        text = Regex.Replace(text, "(DTS:CreatorName=\")[^\"]*", "$1");
        text = Regex.Replace(text, "(DTS:CreatorComputerName=\")[^\"]*", "$1");
        text = Regex.Replace(text, "(<SSIS:Property SSIS:Name=\"CreatorName\">)[^<]*", "$1");
        text = Regex.Replace(text, "(<SSIS:Property SSIS:Name=\"CreatorComputerName\">)[^<]*", "$1");
        // File paths: keep only the file name, under a neutral folder.
        text = Regex.Replace(text, @"[A-Za-z]:\\Users\\[^""<]*?\\(Queries\\)?([^\\""<]+\.sql)", m => @"C:\Templates\" + m.Groups[1].Value + m.Groups[2].Value);
        return text;
    }

    /// <summary>Set the Value of every connection manager parameter CM.&lt;manager&gt;.&lt;prop&gt; in a .dtproj.</summary>
    private static string ParameterValue(string text, string prop, string value)
    {
        var pattern = "(<SSIS:Parameter\\s+SSIS:Name=\"CM\\.[^\"]*\\." + prop + "\">(?:(?!</SSIS:Parameter>).)*?"
                      + "<SSIS:Property SSIS:Name=\"Value\">)[^<]*";
        return Regex.Replace(text, pattern, m => m.Groups[1].Value + value, RegexOptions.Singleline);
    }
}
