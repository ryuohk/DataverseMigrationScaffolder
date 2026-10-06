using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    internal sealed class ConnectionManagerInfo
    {
        public string File;
        public string Name;
        public string Dtsid;
        public string CreationName;
        public List<string> ExpressionProperties;
        public bool Encrypted;
    }

    internal sealed class ProjectSupport
    {
        public List<ConnectionManagerInfo> Connections;
        public HashSet<string> Parameters;
        public string ParamsFile;
        public string ProtectionLevel;
        public string PackageProtectionLevel;
        public List<string> EncryptedFiles = new List<string>();
        // Set when generation switched the project to DontSaveSensitive: the reference's level,
        // and the files whose stored sensitive values were removed.
        public string ConvertedFrom;
        public List<string> ClearedFiles = new List<string>();

        public ConnectionManagerInfo Connection(string name)
        {
            return Connections.FirstOrDefault(c => c.Name.ToLowerInvariant() == name.ToLowerInvariant());
        }

        public ConnectionManagerInfo ConnectionById(string dtsid)
        {
            return Connections.FirstOrDefault(c => c.Dtsid.ToUpperInvariant() == dtsid.ToUpperInvariant());
        }
    }

    /// <summary>
    /// Project-level SSIS support: connection managers, parameters and protection settings.
    /// Connection manager and parameter files are carried verbatim, so their settings,
    /// expressions, parameter bindings and any encrypted values stay exactly as saved. Only
    /// names, IDs and structure are read; property values are never reported.
    /// </summary>
    internal static class Support
    {
        public const string ParamsFileName = "Project.params";
        public const string DontSaveSensitive = "DontSaveSensitive";
        private static readonly Regex GuidRe = Py.Re(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        private static readonly Regex GuidFull = Py.Full(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
        private static readonly Regex ManagerRef = Py.Re(@"\b(Project|Package)\.ConnectionManagers\[([^\]]+)\]");
        private static readonly Regex ParameterRef = Py.Re(@"\$(Project|Package)::([A-Za-z_][A-Za-z0-9_]*)");
        private static readonly Dictionary<string, string> PackageLevels = new Dictionary<string, string>
        {
            { "0", "DontSaveSensitive" }, { "1", "EncryptSensitiveWithUserKey" }, { "2", "EncryptSensitiveWithPassword" },
            { "3", "EncryptAllWithPassword" }, { "4", "EncryptAllWithUserKey" }, { "5", "ServerStorage" },
        };

        private static XmlDocument ParseFile(string path, string what)
        {
            try { return Xml.ParseFile(path); }
            catch (XmlException ex) { throw new GeneratorException(what + " is not well-formed XML (" + Path.GetFileName(path) + "): " + ex.Message, ex); }
        }

        private static bool Flag(XmlElement el, string local)
        {
            return el.Attributes.Cast<XmlAttribute>().Any(a => a.Value == "1" && a.Name.Split(':').Last() == local);
        }

        private static bool HasText(XmlElement el)
        {
            return el.ChildNodes.Cast<XmlNode>().Any(c => Xml.IsTextNode(c) && Xml.Data(c).Trim().Length > 0);
        }

        /// <summary>Any encrypted value below the node (for a document, including its root element).
        /// Some third-party managers (KingswaySoft's ClientSecret, for one) store an encrypted value
        /// marked only Sensitive="1", without Encrypted="1".</summary>
        private static bool IsEncrypted(XmlNode node)
        {
            var below = node is XmlDocument ? Xml.Elements(((XmlDocument)node).DocumentElement) : Xml.Elements(node).Skip(1).ToList();
            return below.Any(el => el.LocalName == "EncryptedData" || Flag(el, "Encrypted") || (Flag(el, "Sensitive") && HasText(el)));
        }

        /// <summary>Remove every stored sensitive value (as Visual Studio does on saving with
        /// DontSaveSensitive); returns how many were removed. Nothing is decrypted.</summary>
        public static int StripSensitive(XmlNode node)
        {
            var removed = 0;
            var root = node is XmlDocument ? ((XmlDocument)node).DocumentElement : (XmlElement)node;
            foreach (var el in Xml.Elements(root))
            {
                if (!Flag(el, "Sensitive")) continue;
                if (HasText(el))
                {
                    removed++;
                    foreach (var child in el.ChildNodes.Cast<XmlNode>().Where(Xml.IsTextNode).ToList()) el.RemoveChild(child);
                }
                foreach (var attr in el.Attributes.Cast<XmlAttribute>().Where(a => a.Name.Split(':').Last() == "Encrypted").ToList())
                    el.Attributes.Remove(attr);
            }
            return removed;
        }

        /// <summary>A file listed by the .dtproj; it must be a plain name inside the project folder.</summary>
        public static string LocalFile(string directory, string name, string what)
        {
            if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name == "." || name == "..")
                throw new GeneratorException(what + " " + Py.Repr(name) + " must be a file in the reference project folder");
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new GeneratorException(what + " listed in the reference .dtproj does not exist: " + path);
            return path;
        }

        public static ProjectSupport Load(string directory, XmlDocument dtproj, byte[] packageXml, string packageLabel, string metadataProtection)
        {
            var project = Xml.ByTagNs(dtproj, Xml.SsisNs, "Project");
            var protection = project.Count > 0 ? Xml.GetNs(project[0], Xml.SsisNs, "ProtectionLevel") : "";
            if (string.IsNullOrEmpty(protection)) protection = "EncryptSensitiveWithUserKey";

            var connections = new List<ConnectionManagerInfo>();
            var encrypted = new List<string>();
            foreach (var entry in Xml.ByTagNs(dtproj, Xml.SsisNs, "ConnectionManager"))
            {
                if (entry.ParentNode == null || entry.ParentNode.LocalName != "ConnectionManagers") continue;
                var fileName = Xml.GetNs(entry, Xml.SsisNs, "Name");
                var path = LocalFile(directory, fileName, "Project connection manager");
                var root = ParseFile(path, "Project connection manager").DocumentElement;
                if (root.NamespaceURI != Xml.DtsNs || root.LocalName != "ConnectionManager")
                    throw new GeneratorException("Project connection manager " + fileName + " cannot be read (root is " + Py.Repr(root.Name)
                                                 + "); EncryptAll protection levels are not supported");
                var name = root.GetAttribute("DTS:ObjectName");
                var dtsid = root.GetAttribute("DTS:DTSID");
                if (name.Length == 0 || !GuidFull.IsMatch(dtsid))
                    throw new GeneratorException("Project connection manager " + fileName + " has no ObjectName or DTSID");
                var expressions = Py.Sorted(Xml.Elements(root).Skip(1).Where(e => e.LocalName == "PropertyExpression" && e.GetAttribute("DTS:Name").Length > 0)
                                                .Select(e => e.GetAttribute("DTS:Name")).Distinct());
                var isEncrypted = IsEncrypted(root);
                if (isEncrypted) encrypted.Add(fileName);
                connections.Add(new ConnectionManagerInfo
                {
                    File = fileName, Name = name, Dtsid = dtsid, CreationName = root.GetAttribute("DTS:CreationName"),
                    ExpressionProperties = expressions, Encrypted = isEncrypted,
                });
            }
            foreach (var check in new[] { Tuple.Create("name", (Func<ConnectionManagerInfo, string>)(c => c.Name.ToLowerInvariant())),
                                          Tuple.Create("DTSID", (Func<ConnectionManagerInfo, string>)(c => c.Dtsid.ToUpperInvariant())) })
            {
                var values = connections.Select(check.Item2).ToList();
                var dupes = Py.Sorted(values.Where(v => values.Count(x => x == v) > 1).Distinct());
                if (dupes.Count > 0)
                    throw new GeneratorException("Reference project has duplicate project connection manager " + check.Item1 + "(s): " + string.Join(", ", dupes));
            }

            var parameters = new HashSet<string>(StringComparer.Ordinal);
            string paramsFile = null;
            if (File.Exists(Path.Combine(directory, ParamsFileName)))
            {
                paramsFile = ParamsFileName;
                var doc = ParseFile(Path.Combine(directory, ParamsFileName), "Project parameters file");
                foreach (var param in Xml.ByTagNs(doc, Xml.SsisNs, "Parameter")) parameters.Add(Xml.GetNs(param, Xml.SsisNs, "Name"));
                if (IsEncrypted(doc)) encrypted.Add(ParamsFileName);
            }

            XmlElement package;
            try { package = Xml.Parse(packageXml).DocumentElement; }
            catch (XmlException ex) { throw new GeneratorException("Reference package " + packageLabel + " is not well-formed XML: " + ex.Message, ex); }
            if (Xml.Elements(package).Skip(1).Any(el => el.LocalName == "EncryptedData"))
                throw new GeneratorException("Reference package " + packageLabel + " is fully encrypted (EncryptAll protection level); save it with "
                                             + "DontSaveSensitive or an EncryptSensitive level to clone it");
            if (IsEncrypted(package)) encrypted.Add(packageLabel);
            var level = package.GetAttribute("DTS:ProtectionLevel");
            if (string.IsNullOrEmpty(level)) level = string.IsNullOrEmpty(metadataProtection) ? "1" : metadataProtection;
            string named;
            var support = new ProjectSupport
            {
                Connections = connections, Parameters = parameters, ParamsFile = paramsFile, ProtectionLevel = protection,
                PackageProtectionLevel = PackageLevels.TryGetValue(level, out named) ? named : level, EncryptedFiles = encrypted,
            };
            CheckTemplateManagers(package, support, packageLabel);
            foreach (var manager in connections)
            {
                var root = ParseFile(Path.Combine(directory, manager.File), "Project connection manager").DocumentElement;
                CheckParameters(root, support, new HashSet<string>(), "project connection manager " + manager.File, "Project");
            }
            return support;
        }

        private static List<XmlElement> LocalManagers(XmlElement root)
        {
            return Xml.Children(root, "ConnectionManagers").SelectMany(c => Xml.Children(c, "ConnectionManager")).ToList();
        }

        /// <summary>Package managers get new IDs per generated package, so a template manager that shares
        /// a project manager's name or ID would silently redirect project references.</summary>
        private static void CheckTemplateManagers(XmlElement root, ProjectSupport support, string label)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var manager in LocalManagers(root))
            {
                var name = manager.GetAttribute("DTS:ObjectName");
                var dtsid = manager.GetAttribute("DTS:DTSID").ToUpperInvariant();
                if (names.Contains(name.ToLowerInvariant()) || (dtsid.Length > 0 && ids.Contains(dtsid)))
                    throw new GeneratorException("Reference package " + label + " has duplicate package connection manager " + Py.Repr(name));
                names.Add(name.ToLowerInvariant());
                ids.Add(dtsid);
                var clash = support.Connection(name) ?? (dtsid.Length > 0 ? support.ConnectionById(dtsid) : null);
                if (clash != null)
                    throw new GeneratorException("Reference package " + label + ": package connection manager " + Py.Repr(name) + " has the same name or DTSID "
                                                 + "as project connection manager " + Py.Repr(clash.Name) + " (" + clash.File + "); rename it or give it a new "
                                                 + "ID in the reference so the two configurations are not conflated");
            }
        }

        private static void CheckParameters(XmlElement root, ProjectSupport support, HashSet<string> packageParameters, string label, string scope = null)
        {
            foreach (var el in Xml.Elements(root))
            {
                var values = el.Attributes.Cast<XmlAttribute>().Select(a => a.Value)
                               .Concat(el.ChildNodes.Cast<XmlNode>().Where(Xml.IsTextOrCData).Select(Xml.Data));
                foreach (var value in values)
                    foreach (Match m in ParameterRef.Matches(value))
                    {
                        var kind = m.Groups[1].Value;
                        var name = m.Groups[2].Value;
                        if (scope != null && kind != scope) throw new GeneratorException(label + ": $" + kind + "::" + name + " cannot be referenced here");
                        var known = kind == "Project" ? support.Parameters : packageParameters;
                        if (!known.Contains(name)) throw new GeneratorException(label + ": parameter binding $" + kind + "::" + name + " does not resolve");
                    }
            }
        }

        public static List<KeyValuePair<string, string>> PackageParameterIds(byte[] xml)
        {
            var root = Xml.Parse(xml).DocumentElement;
            var result = new List<KeyValuePair<string, string>>();
            foreach (var c in Xml.Children(root, "PackageParameters"))
                foreach (var p in Xml.Children(c, "PackageParameter"))
                {
                    var name = p.GetAttribute("DTS:ObjectName");
                    var i = result.FindIndex(kv => kv.Key == name);
                    var entry = new KeyValuePair<string, string>(name, p.GetAttribute("DTS:DTSID"));
                    if (i >= 0) result[i] = entry; else result.Add(entry);
                }
            return result;
        }

        public static HashSet<string> PackageConnectionNames(byte[] xml)
        {
            return new HashSet<string>(LocalManagers(Xml.Parse(xml).DocumentElement).Select(m => m.GetAttribute("DTS:ObjectName")), StringComparer.Ordinal);
        }

        /// <summary>Check that every connection reference and parameter binding in a generated package
        /// resolves; return the connection names it uses (no connection properties).</summary>
        public static JObj VerifyPackage(byte[] xml, ProjectSupport support, string label)
        {
            var root = Xml.Parse(xml).DocumentElement;
            var localByName = new Dictionary<string, string>(StringComparer.Ordinal);
            var localById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var manager in LocalManagers(root))
            {
                var name = manager.GetAttribute("DTS:ObjectName");
                var dtsid = manager.GetAttribute("DTS:DTSID").ToUpperInvariant();
                if (localByName.ContainsKey(name) || localById.ContainsKey(dtsid))
                    throw new GeneratorException("Generated package " + label + ": duplicate package connection manager " + Py.Repr(name));
                if (support.Connection(name) != null || support.ConnectionById(dtsid) != null)
                    throw new GeneratorException("Generated package " + label + ": package connection manager " + Py.Repr(name) + " conflicts with a project connection manager");
                localByName[name] = dtsid;
                localById[dtsid] = name;
            }
            var usedProject = new HashSet<string>(StringComparer.Ordinal);
            Func<string, string, string> resolveRef = (scope, name) =>
            {
                if (scope == "Project")
                {
                    var manager = support.Connection(name);
                    if (manager == null) throw new GeneratorException("Generated package " + label + ": project connection manager " + Py.Repr(name) + " is not in the project");
                    usedProject.Add(manager.Name);
                    return manager.Dtsid.ToUpperInvariant();
                }
                if (!localByName.ContainsKey(name)) throw new GeneratorException("Generated package " + label + ": package connection manager " + Py.Repr(name) + " does not exist");
                return localByName[name];
            };
            Func<string, string> resolveId = value =>
            {
                var dtsid = GuidRe.Match(value).Value.ToUpperInvariant();
                var project = support.ConnectionById(dtsid);
                if (value.ToLowerInvariant().EndsWith(":external", StringComparison.Ordinal))
                {
                    if (project == null) throw new GeneratorException("Generated package " + label + ": external connection " + Py.Repr(value) + " is not a project connection manager");
                }
                else if (project == null && !localById.ContainsKey(dtsid))
                    throw new GeneratorException("Generated package " + label + ": connection " + Py.Repr(value) + " does not resolve");
                if (project != null) usedProject.Add(project.Name);
                return dtsid;
            };

            foreach (var el in Xml.Elements(root).Skip(1))
            {
                if (el.LocalName == "ConnectionManager" && el.ParentNode.LocalName == "ConnectionManagers") continue;
                string byRef = null, byId = null;
                foreach (XmlAttribute attr in el.Attributes)
                {
                    var local = attr.Name.Split(':').Last().ToLowerInvariant();
                    if (local.Contains("string")) continue;   // connection strings are data, not references
                    foreach (Match m in ManagerRef.Matches(attr.Value))
                    {
                        var reference = resolveRef(m.Groups[1].Value, m.Groups[2].Value);
                        if (local == "connectionmanagerrefid") byRef = reference;
                    }
                    if ((local == "connectionmanagerid" || local.EndsWith("connection", StringComparison.Ordinal)) && GuidRe.IsMatch(attr.Value))
                    {
                        var ident = resolveId(attr.Value);
                        if (local == "connectionmanagerid") byId = ident;
                    }
                }
                if (byRef != null && byId != null && byRef != byId)
                    throw new GeneratorException("Generated package " + label + ": connectionManagerID and connectionManagerRefId of "
                                                 + Py.Repr(el.GetAttribute("name").Length > 0 ? el.GetAttribute("name") : el.Name) + " point at different managers");
            }
            var parameters = new HashSet<string>(Xml.Children(root, "PackageParameters").SelectMany(c => Xml.Children(c, "PackageParameter"))
                                                     .Select(m => m.GetAttribute("DTS:ObjectName")), StringComparer.Ordinal);
            CheckParameters(root, support, parameters, "Generated package " + label);
            return new JObj().Add("project", Py.SortedLower(usedProject).Cast<object>().ToList())
                             .Add("package", Py.SortedLower(localByName.Keys).Cast<object>().ToList());
        }

        /// <summary>What the user must do about credentials; states limits without promising decryption.</summary>
        public static List<string> CredentialNotes(ProjectSupport support)
        {
            var level = support.ProtectionLevel;
            var notes = new List<string>();
            if (support.ConvertedFrom != null)
            {
                notes.Add("Protection level changed from " + support.ConvertedFrom + " to DontSaveSensitive: the project and its packages store no "
                          + "passwords or secrets, so anyone can open them. Supply credentials when deploying, through the connection managers' "
                          + "settings in the SSIS catalog (for example CM.<connection manager>.ClientSecret) or a SQL Agent job step, or re-enter "
                          + "them in Visual Studio to run locally (they are not saved).");
                if (support.ClearedFiles.Count > 0) notes.Add("Stored sensitive values removed from: " + string.Join(", ", support.ClearedFiles));
                return notes;
            }
            if (level == "DontSaveSensitive")
                notes.Add("Protection level DontSaveSensitive: the reference stores no passwords or secrets, so the generated project has none either. "
                          + "Supply credentials at run time through parameters or SSIS catalog environments, or re-enter them in Visual Studio.");
            else if (level == "EncryptSensitiveWithUserKey")
                notes.Add("Protection level EncryptSensitiveWithUserKey: encrypted values were copied unchanged and were not decrypted. They are bound "
                          + "to the Windows user and machine that saved the reference, and may not decrypt for anyone else or in the regenerated packages; "
                          + "re-enter credentials or supply them through parameters or SSIS catalog environments if Visual Studio reports decryption failures.");
            else if (level == "EncryptSensitiveWithPassword")
                notes.Add("Protection level EncryptSensitiveWithPassword: encrypted values and the project password verifier were copied unchanged and "
                          + "were not decrypted. Opening the project requires the reference project's password; re-enter credentials if any fail to decrypt.");
            else if (level == "ServerStorage")
                notes.Add("Protection level ServerStorage: credentials depend on the deployment server's storage and are not carried by the project files.");
            else notes.Add("Protection level " + level + ": copied unchanged; harnessgen did not decrypt anything.");
            if (support.EncryptedFiles.Count > 0)
                notes.Add("Files containing encrypted values (copied unchanged): " + string.Join(", ", support.EncryptedFiles));
            if (level != support.PackageProtectionLevel)
                notes.Add("The reference package's protection level (" + support.PackageProtectionLevel + ") differs from the project's (" + level
                          + "); Visual Studio requires them to match before building.");
            return notes;
        }
    }
}
