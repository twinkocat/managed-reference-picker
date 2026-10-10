using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ManagedReferencePicker.Editor
{
    public sealed class ManagedReferenceMigrationWindow : EditorWindow
    {
        private ManagedReferenceTypeRegistry registry;
        private ManagedReferenceMigrationPlan plan;
        private Label status;
        private VisualElement results;
        private Button apply;
        private Button restore;

        [MenuItem("Tools/Managed Reference Picker/Migrations")]
        public static void Open()
        {
            GetWindow<ManagedReferenceMigrationWindow>("Reference Migrations");
        }

        public void CreateGUI()
        {
            var scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            var directory = System.IO.Path.GetDirectoryName(scriptPath).Replace('\\', '/');
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(directory + "/ManagedReferenceMigrationWindow.uxml");
            if (layout == null)
            {
                rootVisualElement.Add(new HelpBox("Migration window layout could not be loaded.", HelpBoxMessageType.Error));
                return;
            }

            layout.CloneTree(rootVisualElement);
            status = rootVisualElement.Q<Label>("status");
            results = rootVisualElement.Q<ScrollView>("results");
            apply = rootVisualElement.Q<Button>("apply");
            restore = rootVisualElement.Q<Button>("restore");
            rootVisualElement.Q<Button>("scan").clicked += Scan;
            apply.clicked += Apply;
            restore.clicked += Restore;
            Scan();
        }

        private void Scan()
        {
            Run(() =>
            {
                registry = ManagedReferenceTypeRegistry.Load();
                registry.Refresh(ManagedReferenceTypeDiscovery.Discover());
                registry.Save();
                plan = ManagedReferenceMigration.Scan(registry);
                Render();
            });
        }

        private void Render()
        {
            results.Clear();
            status.text = registry.entries.Count + " tracked types; " + plan.changes.Count + " assets to migrate; "
                + plan.changes.Sum(c => c.referenceCount) + " references.";
            apply.SetEnabled(plan.changes.Count > 0);
            restore.SetEnabled(ManagedReferenceMigration.HasBackup);

            var mappings = plan.mappings;
            if (plan.changes.Count > 0)
            {
                var changes = new Foldout { text = "Migration preview", value = true };
                foreach (var mapping in mappings)
                {
                    changes.Add(WrappedLabel(mapping.Key.Key + " → " + mapping.Value.Key));
                }

                foreach (var change in plan.changes)
                {
                    changes.Add(WrappedLabel(change.path + " (" + change.referenceCount + " references)"));
                }

                results.Add(changes);
            }
            else
            {
                results.Add(new HelpBox("No known type migrations are needed in text assets.", HelpBoxMessageType.Info));
            }

            if (plan.issues.Count > 0)
            {
                var issues = new Foldout { text = "Unresolved references", value = true };
                foreach (var issue in plan.issues)
                {
                    issues.Add(WrappedLabel(issue));
                }

                results.Add(issues);
            }

            var missing = registry.entries.Where(e => e.current.Resolve() == null).ToList();
            foreach (var identity in plan.missingTypes)
            {
                if (!missing.Any(e => e.current.Equals(identity)))
                {
                    missing.Add(new ManagedReferenceTypeEntry
                    {
                        current = identity,
                        scriptPath = "Not recorded in the registry"
                    });
                }
            }

            if (missing.Count > 0)
            {
                var choices = registry.entries.Where(e => e.current.Resolve() != null)
                    .OrderBy(e => e.current.Key, StringComparer.Ordinal).ToArray();
                var unresolved = new Foldout { text = "Associate missing types", value = true };
                unresolved.Add(new HelpBox("Choose a replacement for a class moved to another file or renamed in a file with multiple classes. Use a compatible type; field and behavior changes need a separate migration.", HelpBoxMessageType.Info));
                foreach (var entry in missing)
                {
                    var row = new VisualElement();
                    row.AddToClassList("migration-entry");
                    row.Add(WrappedLabel(entry.current.Key));
                    row.Add(WrappedLabel("Last script: " + entry.scriptPath));
                    var names = new List<string> { "Choose replacement…" };
                    names.AddRange(choices.Select(e => e.current.Key));
                    var picker = new PopupField<string>("Replacement", names, 0);
                    row.Add(picker);
                    var button = new Button(() => Run(() =>
                    {
                        var replacement = choices[picker.index - 1];
                        registry.Associate(entry, replacement);
                        registry.Save();
                        Scan();
                    })) { text = "Associate and preview" };
                    button.SetEnabled(false);
                    picker.RegisterValueChangedCallback(_ => button.SetEnabled(picker.index > 0));
                    row.Add(button);
                    unresolved.Add(row);
                }

                results.Add(unresolved);
            }

            if (plan.binaryAssets > 0)
            {
                results.Add(new HelpBox(plan.binaryAssets + " binary assets were skipped. This tool migrates Unity text assets with reference format version 2. Keep asset serialization set to Force Text.", HelpBoxMessageType.Warning));
            }
        }

        private void Apply()
        {
            Run(() =>
            {
                var directory = ManagedReferenceMigration.Apply(plan);
                Scan();
                status.text = "Migration complete. Backup: " + directory;
            });
        }

        private void Restore()
        {
            Run(() =>
            {
                ManagedReferenceMigration.RestoreLastBackup();
                Scan();
                status.text = "Restored the last migration. Type history is retained.";
            });
        }

        private void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                status.text = exception.Message;
                apply.SetEnabled(false);
                Debug.LogError("Managed reference migration: " + exception.Message);
            }
        }

        private static Label WrappedLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("migration-description");
            return label;
        }
    }
}
