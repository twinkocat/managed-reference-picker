using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ManagedReferencePicker.Editor
{
    [Serializable]
    internal sealed class ManagedReferenceTypeIdentity : IEquatable<ManagedReferenceTypeIdentity>
    {
        public string assembly;
        public string ns;
        public string name;

        public string FullName => string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        public string Key => assembly + " " + FullName;

        public static ManagedReferenceTypeIdentity FromType(Type type)
        {
            var prefix = string.IsNullOrEmpty(type.Namespace) ? 0 : type.Namespace.Length + 1;
            return new ManagedReferenceTypeIdentity
            {
                assembly = type.Assembly.GetName().Name,
                ns = type.Namespace ?? string.Empty,
                name = type.FullName.Substring(prefix).Replace('+', '/')
            };
        }

        public static ManagedReferenceTypeIdentity FromUnityTypename(string typename)
        {
            var separator = typename.IndexOf(' ');
            if (separator <= 0 || separator == typename.Length - 1)
            {
                return null;
            }

            var fullName = typename.Substring(separator + 1).Trim();
            var outerEnd = fullName.IndexOfAny(new[] { '/', '[' });
            var outerName = outerEnd < 0 ? fullName : fullName.Substring(0, outerEnd);
            var namespaceEnd = outerName.LastIndexOf('.');
            return new ManagedReferenceTypeIdentity
            {
                assembly = typename.Substring(0, separator),
                ns = namespaceEnd < 0 ? string.Empty : fullName.Substring(0, namespaceEnd),
                name = fullName.Substring(namespaceEnd + 1)
            };
        }

        public Type Resolve()
        {
            return Type.GetType(FullName.Replace('/', '+') + ", " + assembly, false);
        }

        public bool Equals(ManagedReferenceTypeIdentity other)
        {
            return other != null && assembly == other.assembly && ns == other.ns && name == other.name;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ManagedReferenceTypeIdentity);
        }

        public override int GetHashCode()
        {
            return Key.GetHashCode();
        }
    }

    [Serializable]
    internal sealed class ManagedReferenceTypeEntry
    {
        public string scriptGuid;
        public string scriptPath;
        public ManagedReferenceTypeIdentity current;
        public List<ManagedReferenceTypeIdentity> history = new();
    }

    [Serializable]
    internal sealed class ManagedReferenceTypeRegistry
    {
        internal const string FilePath = "ProjectSettings/ManagedReferencePickerTypes.json";

        public int version = 1;
        public List<ManagedReferenceTypeEntry> entries = new();

        public static ManagedReferenceTypeRegistry Load()
        {
            if (!File.Exists(FilePath))
            {
                return new ManagedReferenceTypeRegistry();
            }

            var registry = JsonUtility.FromJson<ManagedReferenceTypeRegistry>(File.ReadAllText(FilePath));
            if (registry == null || registry.version != 1 || registry.entries == null
                || registry.entries.Any(e => e == null || e.current == null || e.history == null))
            {
                throw new InvalidDataException("Invalid managed-reference registry: " + FilePath);
            }

            return registry;
        }

        public void Save()
        {
            entries = entries.OrderBy(e => e.scriptGuid, StringComparer.Ordinal)
                .ThenBy(e => e.current.Key, StringComparer.Ordinal).ToList();
            foreach (var entry in entries)
            {
                entry.history = entry.history.Distinct().OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
            }

            var json = JsonUtility.ToJson(this, true).Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
            if (!File.Exists(FilePath) || File.ReadAllText(FilePath) != json)
            {
                var temporaryPath = FilePath + ".tmp";
                File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    File.Replace(temporaryPath, FilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, FilePath);
                }
            }
        }

        public void Refresh(IReadOnlyList<ManagedReferenceTypeEntry> discovered)
        {
            // Take a snapshot so newly registered siblings cannot change rename inference.
            var previous = entries.ToArray();
            var currentKeys = new HashSet<string>(discovered.Select(e => e.current.Key));
            var claimed = new HashSet<ManagedReferenceTypeEntry>();
            foreach (var source in discovered.OrderBy(e => e.current.Key, StringComparer.Ordinal))
            {
                var exact = previous.Where(e => e.current.Equals(source.current)).ToArray();
                ManagedReferenceTypeEntry entry = exact.Length == 1 ? exact[0] : null;
                if (entry == null)
                {
                    var oldSiblings = previous.Where(e => e.scriptGuid == source.scriptGuid).ToArray();
                    var newSiblings = discovered.Where(e => e.scriptGuid == source.scriptGuid).ToArray();
                    var matches = oldSiblings.Where(e => !claimed.Contains(e)
                        && !currentKeys.Contains(e.current.Key) && e.current.name == source.current.name).ToArray();
                    if (matches.Length == 1
                        && newSiblings.Count(e => e.current.name == source.current.name) == 1)
                    {
                        entry = matches[0];
                    }
                    else if (oldSiblings.Length == 1 && newSiblings.Length == 1
                        && !currentKeys.Contains(oldSiblings[0].current.Key) && !claimed.Contains(oldSiblings[0]))
                    {
                        entry = oldSiblings[0];
                    }
                }

                if (entry == null)
                {
                    entry = new ManagedReferenceTypeEntry { current = source.current };
                    entries.Add(entry);
                }
                else if (!entry.current.Equals(source.current))
                {
                    entry.history.Add(entry.current);
                    entry.current = source.current;
                }

                entry.scriptGuid = source.scriptGuid;
                entry.scriptPath = source.scriptPath;
                claimed.Add(entry);
            }
        }

        public void Associate(ManagedReferenceTypeEntry previous, ManagedReferenceTypeEntry replacement)
        {
            if (previous == replacement || previous.current.Resolve() != null)
            {
                throw new InvalidOperationException("Only a missing type can be associated with a replacement.");
            }

            if (replacement.current.Resolve() == null)
            {
                throw new InvalidOperationException("The replacement type is unavailable.");
            }

            replacement.history.Add(previous.current);
            replacement.history.AddRange(previous.history);
            entries.Remove(previous);
        }

        public Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> GetMappings(List<string> issues)
        {
            var candidates = entries.Where(e => e.current.Resolve() != null)
                .SelectMany(e => e.history.Where(h => !h.Equals(e.current) && h.Resolve() == null)
                    .Select(h => new { previous = h, replacement = e.current }));
            var result = new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity>();
            foreach (var group in candidates.GroupBy(c => c.previous))
            {
                var replacements = group.Select(c => c.replacement).Distinct().ToArray();
                if (replacements.Length != 1)
                {
                    issues.Add("Conflicting history for " + group.Key.Key + ". Resolve the registry conflict first.");
                    continue;
                }

                result.Add(group.Key, replacements[0]);
            }

            return result;
        }
    }

    [InitializeOnLoad]
    internal static class ManagedReferenceRegistryInitialization
    {
        static ManagedReferenceRegistryInitialization()
        {
            EditorApplication.delayCall += Refresh;
        }

        private static void Refresh()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Refresh;
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            try
            {
                var registry = ManagedReferenceTypeRegistry.Load();
                registry.Refresh(ManagedReferenceTypeDiscovery.Discover());
                registry.Save();
                var plan = ManagedReferenceMigration.Scan(registry);
                var signature = string.Join("\n", plan.changes.Select(c => c.path).Concat(plan.issues));
                const string notificationKey = "ManagedReferencePicker.MigrationNotification";
                if (signature.Length > 0 && SessionState.GetString(notificationKey, string.Empty) != signature)
                {
                    SessionState.SetString(notificationKey, signature);
                    Debug.LogWarning("Managed reference type changes detected. Review Tools > Managed Reference Picker > Migrations.");
                }
                else if (signature.Length == 0)
                {
                    SessionState.EraseString(notificationKey);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("Managed reference registry could not be refreshed: " + exception.Message);
            }
        }
    }
}
