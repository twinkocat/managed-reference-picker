using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ManagedReferencePicker.Editor
{
    internal sealed class ManagedReferenceAssetChange
    {
        public string path;
        public byte[] original;
        public byte[] migrated;
        public int referenceCount;
    }

    internal sealed class ManagedReferenceMigrationPlan
    {
        public readonly List<ManagedReferenceAssetChange> changes = new();
        public readonly List<string> issues = new();
        public readonly List<ManagedReferenceTypeIdentity> missingTypes = new();
        public readonly Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> mappings = new();
        public int binaryAssets;
    }

    internal static class ManagedReferenceYaml
    {
        private const string scalar = "(?:'(?:''|[^'])*'|\"(?:\\\\.|[^\"\\\\])*\"|[^,{}\\r\\n]*)";
        private static readonly Regex identityPattern = new(
            @"\btype: *\{class: *(?<class>" + scalar + @"),\s*ns: *(?<ns>" + scalar
            + @"),\s*asm: *(?<asm>" + scalar + @")\}", RegexOptions.CultureInvariant);
        private static readonly Regex referenceHeaderPattern = new(@"^  references:\r?$", RegexOptions.Multiline);
        private static readonly Regex entryPattern = new(@"^    - rid: -?\d+\r?\n      ", RegexOptions.Multiline);
        private static readonly Regex prefabHeaderPattern = new(@"^PrefabInstance:\r?$", RegexOptions.Multiline);
        private static readonly Regex typeOverridePattern = new(
            "^    - target: \\{[^\\r\\n]+\\}\\r?\\n"
            + "      propertyPath: ['\"]?managedReferences\\[-?\\d+\\]['\"]?\\r?\\n"
            + "      value: (?<typename>[^\\r\\n]*(?:\\r?\\n        [^\\r\\n]+)*)\\r?\\n"
            + "      objectReference: \\{fileID: 0\\}", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        internal static string Rewrite(string yaml,
            IReadOnlyDictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> mappings,
            out int count, List<ManagedReferenceTypeIdentity> missing = null,
            HashSet<ManagedReferenceTypeIdentity> used = null)
        {
            var replacements = new List<(int start, int length, string text)>();
            foreach (Match header in referenceHeaderPattern.Matches(yaml))
            {
                var end = yaml.IndexOf("\n--- !u!", header.Index, StringComparison.Ordinal);
                var block = yaml.Substring(header.Index, (end < 0 ? yaml.Length : end) - header.Index);
                if (!Regex.IsMatch(block, @"\A  references:\r?\n    version: 2\r?\n    RefIds:\r?\n"))
                {
                    continue;
                }

                foreach (Match entry in entryPattern.Matches(block))
                {
                    var start = entry.Index + entry.Length;
                    var identity = identityPattern.Match(block, start);
                    if (!identity.Success || identity.Index != start)
                    {
                        continue;
                    }

                    var previous = new ManagedReferenceTypeIdentity
                    {
                        assembly = Unquote(identity.Groups["asm"].Value),
                        ns = Unquote(identity.Groups["ns"].Value),
                        name = Unquote(identity.Groups["class"].Value)
                    };
                    if (string.IsNullOrEmpty(previous.name) || string.IsNullOrEmpty(previous.assembly))
                    {
                        continue;
                    }

                    if (mappings.TryGetValue(previous, out var replacement))
                    {
                        used?.Add(previous);
                        replacements.Add((header.Index + identity.Index, identity.Length,
                            "type: {class: " + Quote(replacement.name) + ", ns: " + Quote(replacement.ns)
                            + ", asm: " + Quote(replacement.assembly) + "}"));
                    }
                    else if (missing != null && previous.Resolve() == null && !missing.Contains(previous))
                    {
                        missing.Add(previous);
                    }
                }
            }

            foreach (Match header in prefabHeaderPattern.Matches(yaml))
            {
                var end = yaml.IndexOf("\n--- !u!", header.Index, StringComparison.Ordinal);
                var block = yaml.Substring(header.Index, (end < 0 ? yaml.Length : end) - header.Index);
                foreach (Match modification in typeOverridePattern.Matches(block))
                {
                    var value = modification.Groups["typename"];
                    var typename = Unquote(Regex.Replace(value.Value, @"\r?\n +", " "));
                    var previous = ManagedReferenceTypeIdentity.FromUnityTypename(typename);
                    if (previous == null)
                    {
                        continue;
                    }

                    if (mappings.TryGetValue(previous, out var replacement))
                    {
                        used?.Add(previous);
                        replacements.Add((header.Index + value.Index, value.Length, Quote(replacement.Key)));
                    }
                    else if (missing != null && previous.Resolve() == null && !missing.Contains(previous))
                    {
                        missing.Add(previous);
                    }
                }
            }

            count = replacements.Count;
            var result = new StringBuilder(yaml);
            foreach (var replacement in replacements.OrderByDescending(r => r.start))
            {
                result.Remove(replacement.start, replacement.length);
                result.Insert(replacement.start, replacement.text);
            }

            return result.ToString();
        }

        private static string Unquote(string value)
        {
            value = value.Trim();
            if (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal))
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            }

            if (value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
            {
                return Regex.Unescape(value.Substring(1, value.Length - 2));
            }

            return value;
        }

        private static string Quote(string value)
        {
            return string.IsNullOrEmpty(value) || Regex.IsMatch(value, @"^[\p{L}\p{N}_./+`-]+$")
                ? value : "'" + value.Replace("'", "''") + "'";
        }
    }

    internal static class ManagedReferenceMigration
    {
        private const string backupRoot = "Library/ManagedReferencePicker/Backups";
        private static readonly HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".asset", ".prefab", ".unity", ".controller", ".overrideController", ".playable"
        };

        [Serializable]
        private sealed class BackupManifest
        {
            public List<string> paths = new();
        }

        public static ManagedReferenceMigrationPlan Scan(ManagedReferenceTypeRegistry registry,
            IEnumerable<string> paths = null)
        {
            var plan = new ManagedReferenceMigrationPlan();
            var mappings = registry.GetMappings(plan.issues);
            foreach (var path in paths ?? AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !extensions.Contains(Path.GetExtension(path))
                    || !File.Exists(path))
                {
                    continue;
                }

                var original = File.ReadAllBytes(path);
                var hasBom = original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf;
                var offset = hasBom ? 3 : 0;
                if (original.Length < offset + 5 || Encoding.ASCII.GetString(original, offset, 5) != "%YAML")
                {
                    plan.binaryAssets++;
                    continue;
                }

                var text = new UTF8Encoding(false, true).GetString(original, offset, original.Length - offset);
                var missing = new List<ManagedReferenceTypeIdentity>();
                var used = new HashSet<ManagedReferenceTypeIdentity>();
                var migrated = ManagedReferenceYaml.Rewrite(text, mappings, out var count, missing, used);
                foreach (var identity in used)
                {
                    plan.mappings[identity] = mappings[identity];
                }
                plan.issues.AddRange(missing.Select(t => path + ": missing " + t.Key));
                foreach (var identity in missing)
                {
                    if (!plan.missingTypes.Contains(identity))
                    {
                        plan.missingTypes.Add(identity);
                    }
                }
                if (count == 0)
                {
                    continue;
                }

                var encoding = new UTF8Encoding(hasBom);
                plan.changes.Add(new ManagedReferenceAssetChange
                {
                    path = path,
                    original = original,
                    migrated = encoding.GetPreamble().Concat(encoding.GetBytes(migrated)).ToArray(),
                    referenceCount = count
                });
            }

            return plan;
        }

        public static string Apply(ManagedReferenceMigrationPlan plan)
        {
            if (plan.changes.Count == 0)
            {
                throw new InvalidOperationException("There are no references to migrate.");
            }

            Validate(plan.changes);
            var directory = backupRoot + "/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            var manifest = new BackupManifest();
            foreach (var change in plan.changes)
            {
                WriteBackup(directory + "/original/" + change.path, change.original);
                WriteBackup(directory + "/migrated/" + change.path, change.migrated);
                manifest.paths.Add(change.path);
            }

            File.WriteAllText(directory + "/manifest.json", JsonUtility.ToJson(manifest, true));
            Execute(plan.changes);
            File.WriteAllText(backupRoot + "/latest.txt", directory);
            return directory;
        }

        public static bool HasBackup => File.Exists(backupRoot + "/latest.txt");

        public static void RestoreLastBackup()
        {
            var directory = File.ReadAllText(backupRoot + "/latest.txt");
            var fullBackupRoot = Path.GetFullPath(backupRoot) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(fullBackupRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Invalid migration backup path.");
            }

            var manifest = JsonUtility.FromJson<BackupManifest>(File.ReadAllText(directory + "/manifest.json"));
            if (manifest == null || manifest.paths == null || manifest.paths.Count == 0)
            {
                throw new InvalidDataException("Invalid migration backup manifest.");
            }

            foreach (var path in manifest.paths)
            {
                ValidateAssetPath(path);
            }

            var changes = manifest.paths.Select(path => new ManagedReferenceAssetChange
            {
                path = path,
                original = File.ReadAllBytes(directory + "/migrated/" + path),
                migrated = File.ReadAllBytes(directory + "/original/" + path)
            }).ToList();
            Validate(changes);
            Execute(changes);
            File.Delete(backupRoot + "/latest.txt");
        }

        private static void Validate(IReadOnlyList<ManagedReferenceAssetChange> changes)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Wait for the Editor to finish compiling and leave Play mode before migrating.");
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("Save or discard your scene changes before migrating.");
                }
            }

            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                throw new InvalidOperationException("Close Prefab mode before migrating.");
            }

            foreach (var change in changes)
            {
                ValidateAssetPath(change.path);

                if (!File.Exists(change.path) || !File.ReadAllBytes(change.path).SequenceEqual(change.original))
                {
                    throw new InvalidOperationException("Asset changed since the preview. Scan again: " + change.path);
                }

                if ((File.GetAttributes(change.path) & FileAttributes.ReadOnly) != 0 || !AssetDatabase.IsOpenForEdit(change.path))
                {
                    throw new InvalidOperationException("Asset is not writable: " + change.path);
                }

                if (!string.Equals(Path.GetExtension(change.path), ".unity", StringComparison.OrdinalIgnoreCase)
                    && AssetDatabase.LoadAllAssetsAtPath(change.path).Any(EditorUtility.IsDirty))
                {
                    throw new InvalidOperationException("Save or discard your asset changes before migrating: " + change.path);
                }
            }
        }

        private static void ValidateAssetPath(string path)
        {
            var root = Path.GetFullPath("Assets") + Path.DirectorySeparatorChar;
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)
                || !Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !extensions.Contains(Path.GetExtension(path)))
            {
                throw new InvalidOperationException("Migration path must be a Unity asset under Assets: " + path);
            }
        }

        private static void Execute(IReadOnlyList<ManagedReferenceAssetChange> changes)
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var closeScenes = setup.Any(s => changes.Any(c => c.path == s.path));
            var written = new List<ManagedReferenceAssetChange>();
            if (closeScenes)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }

            try
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var change in changes)
                    {
                        written.Add(change);
                        File.WriteAllBytes(change.path, change.migrated);
                    }
                }
                catch
                {
                    foreach (var change in written)
                    {
                        File.WriteAllBytes(change.path, change.original);
                    }

                    throw;
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    foreach (var change in written)
                    {
                        AssetDatabase.ImportAsset(change.path, ImportAssetOptions.ForceUpdate);
                    }
                }
            }
            finally
            {
                if (closeScenes)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
            }
        }

        private static void WriteBackup(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }
    }

    internal sealed class ManagedReferenceMigrationBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            var registry = ManagedReferenceTypeRegistry.Load();
            registry.Refresh(ManagedReferenceTypeDiscovery.Discover());
            registry.Save();
            var plan = ManagedReferenceMigration.Scan(registry);
            if (plan.changes.Count > 0 || plan.issues.Count > 0)
            {
                throw new BuildFailedException("Managed references need migration. Open Tools > Managed Reference Picker > Migrations.\n"
                    + string.Join("\n", plan.issues.Take(10)));
            }
        }
    }
}
