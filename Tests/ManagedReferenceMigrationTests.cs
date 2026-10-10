using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using ManagedReferencePicker.Tests;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ManagedReferencePicker.Editor.Tests
{
    public class ManagedReferenceMigrationTests
    {
        private static ManagedReferenceTypeIdentity Identity(string name, string ns = "Old.Namespace", string assembly = "Old.Assembly")
        {
            return new ManagedReferenceTypeIdentity { name = name, ns = ns, assembly = assembly };
        }

        private static ManagedReferenceTypeEntry Entry(string name, string guid = "script", string ns = "Old.Namespace", string assembly = "Old.Assembly")
        {
            return new ManagedReferenceTypeEntry
            {
                scriptGuid = guid,
                scriptPath = "Assets/" + name + ".cs",
                current = Identity(name, ns, assembly)
            };
        }

        [Test]
        public void Refresh_SingleClassRename_RetainsHistory()
        {
            var registry = new ManagedReferenceTypeRegistry { entries = new List<ManagedReferenceTypeEntry> { Entry("Before") } };
            registry.Refresh(new[] { Entry("After") });
            Assert.That(registry.entries, Has.Count.EqualTo(1));
            Assert.That(registry.entries[0].current.name, Is.EqualTo("After"));
            Assert.That(registry.entries[0].history, Has.Member(Identity("Before")));
        }

        [Test]
        public void Refresh_MultipleClassesNamespaceAndAssemblyMove_MatchesEachClass()
        {
            var registry = new ManagedReferenceTypeRegistry
            {
                entries = new List<ManagedReferenceTypeEntry> { Entry("First"), Entry("Second") }
            };
            registry.Refresh(new[] { Entry("First", ns: "New.Namespace", assembly: "New.Assembly"),
                Entry("Second", ns: "New.Namespace", assembly: "New.Assembly") });
            Assert.That(registry.entries, Has.Count.EqualTo(2));
            Assert.That(registry.entries.All(e => e.history.Count == 1 && e.current.assembly == "New.Assembly"), Is.True);
        }

        [Test]
        public void Refresh_MultiClassRename_DoesNotGuessEvenWithOneUnmatchedSibling()
        {
            var registry = new ManagedReferenceTypeRegistry
            {
                entries = new List<ManagedReferenceTypeEntry> { Entry("First"), Entry("Second") }
            };
            registry.Refresh(new[] { Entry("First"), Entry("Renamed") });
            Assert.That(registry.entries, Has.Count.EqualTo(3));
            Assert.That(registry.entries.All(e => e.history.Count == 0), Is.True);
        }

        [Test]
        public void Refresh_TypeMovedBetweenFilesWithSameIdentity_UpdatesGuid()
        {
            var registry = new ManagedReferenceTypeRegistry { entries = new List<ManagedReferenceTypeEntry> { Entry("First", "before") } };
            registry.Refresh(new[] { Entry("First", "after") });
            Assert.That(registry.entries, Has.Count.EqualTo(1));
            Assert.That(registry.entries[0].scriptGuid, Is.EqualTo("after"));
        }

        [Test]
        public void Refresh_MissingFile_DoesNotDeleteHistory()
        {
            var registry = new ManagedReferenceTypeRegistry { entries = new List<ManagedReferenceTypeEntry> { Entry("First") } };
            registry.Refresh(Array.Empty<ManagedReferenceTypeEntry>());
            Assert.That(registry.entries, Has.Count.EqualTo(1));
        }

        [Test]
        public void Refresh_NewTypeWithReusedOldIdentity_DoesNotInferRename()
        {
            var registry = new ManagedReferenceTypeRegistry { entries = new List<ManagedReferenceTypeEntry> { Entry("Before") } };
            registry.Refresh(new[] { Entry("After"), Entry("Before", "different-file") });
            Assert.That(registry.entries, Has.Count.EqualTo(2));
            Assert.That(registry.entries.All(e => e.history.Count == 0), Is.True);
        }

        [Test]
        public void Refresh_RepeatedRenames_RetainsEveryPreviousIdentity()
        {
            var registry = new ManagedReferenceTypeRegistry { entries = new List<ManagedReferenceTypeEntry> { Entry("First") } };
            registry.Refresh(new[] { Entry("Second") });
            registry.Refresh(new[] { Entry("Third") });
            registry.Refresh(new[] { Entry("Third") });
            Assert.That(registry.entries, Has.Count.EqualTo(1));
            Assert.That(registry.entries[0].history, Is.EquivalentTo(new[] { Identity("First"), Identity("Second") }));
        }

        [Test]
        public void GetMappings_ConflictingHistory_ReportsIssueAndSkipsMapping()
        {
            var registry = new ManagedReferenceTypeRegistry();
            registry.entries.Add(new ManagedReferenceTypeEntry { current = ManagedReferenceTypeIdentity.FromType(typeof(MigrationNode)), history = new List<ManagedReferenceTypeIdentity> { Identity("Before") } });
            registry.entries.Add(new ManagedReferenceTypeEntry { current = ManagedReferenceTypeIdentity.FromType(typeof(ManagedReferenceMigrationTests)), history = new List<ManagedReferenceTypeIdentity> { Identity("Before") } });
            var issues = new List<string>();
            Assert.That(registry.GetMappings(issues), Is.Empty);
            Assert.That(issues, Has.Count.EqualTo(1));
        }

        [Test]
        public void GetMappings_OldIdentityStillResolves_DoesNotRedirectExistingType()
        {
            var registry = new ManagedReferenceTypeRegistry();
            registry.entries.Add(new ManagedReferenceTypeEntry { current = ManagedReferenceTypeIdentity.FromType(typeof(MigrationNode)), history = new List<ManagedReferenceTypeIdentity> { ManagedReferenceTypeIdentity.FromType(typeof(ManagedReferenceMigrationTests)) } });
            Assert.That(registry.GetMappings(new List<string>()), Is.Empty);
        }

        [Test]
        public void Associate_ExplicitReplacement_MergesEntireHistory()
        {
            var previous = Entry("Before");
            previous.history.Add(Identity("Original"));
            var replacement = new ManagedReferenceTypeEntry { current = ManagedReferenceTypeIdentity.FromType(typeof(MigrationNode)) };
            var registry = new ManagedReferenceTypeRegistry { entries = new List<ManagedReferenceTypeEntry> { previous, replacement } };
            registry.Associate(previous, replacement);
            Assert.That(registry.entries, Has.Count.EqualTo(1));
            Assert.That(registry.GetMappings(new List<string>()), Has.Count.EqualTo(2));
        }

        [Test]
        public void GetDeclarations_NamespacesNestedAndMultipleClasses_TracksFullNames()
        {
            var declarations = ManagedReferenceTypeDiscovery.GetDeclarations("namespace Outer { namespace Inner { class First { class Nested {} } class Second {} } } namespace Other { class Third {} }");
            Assert.That(declarations, Is.EquivalentTo(new[] { "Outer.Inner.First", "Outer.Inner.First+Nested", "Outer.Inner.Second", "Other.Third" }));
        }

        [Test]
        public void GetDeclarations_CommentsStringsRawStringsAndFileScopedNamespace_IgnoresFakeClasses()
        {
            var source = "namespace Actual; // class Fake {}\n class @Real { string a = \"class Wrong {}\"; string b = @\"class AlsoWrong {}\"; string c = \"\"\"class RawWrong {}\"\"\"; /* class CommentWrong {} */ }";
            Assert.That(ManagedReferenceTypeDiscovery.GetDeclarations(source), Is.EqualTo(new[] { "Actual.Real" }));
        }

        [Test]
        public void GetDeclarations_GenericConstraint_DoesNotTreatConstraintAsDeclaration()
        {
            Assert.That(ManagedReferenceTypeDiscovery.GetDeclarations("namespace Actual { class Generic<T> where T : class, new() {} class Real {} }"), Is.EqualTo(new[] { "Actual.Real" }));
        }

        private static string Yaml(string identity)
        {
            return "%YAML 1.1\r\n--- !u!114 &1\r\nMonoBehaviour:\r\n  field: {rid: 42}\r\n  references:\r\n    version: 2\r\n    RefIds:\r\n    - rid: 42\r\n      type: " + identity + "\r\n      data:\r\n        value: 123\r\n        text: OldType\r\n        target: {fileID: 11400000, guid: abc, type: 2}\r\n";
        }

        [Test]
        public void Rewrite_RenamedType_PreservesIdsPayloadAndLineEndings()
        {
            var before = "{class: OldType, ns: Old.Namespace, asm: Old.Assembly}";
            var after = "{class: NewType, ns: New.Namespace, asm: New.Assembly}";
            var mappings = new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> { [Identity("OldType")] = Identity("NewType", "New.Namespace", "New.Assembly") };
            var result = ManagedReferenceYaml.Rewrite(Yaml(before), mappings, out var count);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(result, Is.EqualTo(Yaml(after)));
        }

        [Test]
        public void Rewrite_SimilarMetadataInUserPayload_LeavesItUntouched()
        {
            var text = Yaml("{class: OldType, ns: Old.Namespace, asm: Old.Assembly}")
                + "        type: {class: OldType, ns: Old.Namespace, asm: Old.Assembly}\r\n";
            var mappings = new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> { [Identity("OldType")] = Identity("NewType") };
            var result = ManagedReferenceYaml.Rewrite(text, mappings, out var count);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(result, Does.EndWith("        type: {class: OldType, ns: Old.Namespace, asm: Old.Assembly}\r\n"));
        }

        [Test]
        public void Rewrite_WrappedQuotedNestedType_HandlesUnityFormatting()
        {
            var text = Yaml("{class: 'Parent/OldType', ns: 'Old.Namespace',\r\n        asm: 'Old.Assembly'}");
            var mappings = new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> { [Identity("Parent/OldType")] = Identity("Parent/NewType") };
            var result = ManagedReferenceYaml.Rewrite(text, mappings, out var count);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(result, Is.EqualTo(Yaml("{class: Parent/NewType, ns: Old.Namespace, asm: Old.Assembly}")));
        }

        [Test]
        public void Rewrite_MultipleDocuments_MigratesEachReferenceOnce()
        {
            var text = Yaml("{class: OldType, ns: Old.Namespace, asm: Old.Assembly}");
            var mappings = new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> { [Identity("OldType")] = Identity("NewType") };
            ManagedReferenceYaml.Rewrite(text + text.Substring(text.IndexOf("---", StringComparison.Ordinal)), mappings, out var count);
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void Rewrite_UnregisteredMissingType_ReportsWithoutChangingData()
        {
            var text = Yaml("{class: UnknownType, ns: Old.Namespace, asm: Old.Assembly}");
            var missing = new List<ManagedReferenceTypeIdentity>();
            var result = ManagedReferenceYaml.Rewrite(text, new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity>(), out var count, missing);
            Assert.That(result, Is.EqualTo(text));
            Assert.That(count, Is.Zero);
            Assert.That(missing, Has.Member(Identity("UnknownType")));
        }

        [Test]
        public void Rewrite_UnsupportedReferenceVersion_DoesNotModifyData()
        {
            var text = Yaml("{class: OldType, ns: Old.Namespace, asm: Old.Assembly}").Replace("version: 2", "version: 1");
            var mappings = new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> { [Identity("OldType")] = Identity("NewType") };
            Assert.That(ManagedReferenceYaml.Rewrite(text, mappings, out var count), Is.EqualTo(text));
            Assert.That(count, Is.Zero);
        }

        [Test]
        public void Rewrite_PrefabTypeOverride_PreservesFieldOverridesAndReferenceId()
        {
            var text = "%YAML 1.1\n--- !u!1001 &1\nPrefabInstance:\n  m_Modification:\n    m_Modifications:\n"
                + "    - target: {fileID: 1, guid: abc, type: 3}\n      propertyPath: 'managedReferences[42]'\n"
                + "      value: Old.Assembly Old.Namespace.Parent/OldType\n      objectReference: {fileID: 0}\n"
                + "    - target: {fileID: 1, guid: abc, type: 3}\n      propertyPath: managedReferences[42].text\n"
                + "      value: Old.Assembly Old.Namespace.Parent/OldType\n      objectReference: {fileID: 0}\n";
            var mappings = new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity>
            {
                [Identity("Parent/OldType")] = Identity("NewType", "New.Namespace", "New.Assembly")
            };
            var rewritten = ManagedReferenceYaml.Rewrite(text, mappings, out var count);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(rewritten, Does.Contain("value: 'New.Assembly New.Namespace.NewType'"));
            Assert.That(rewritten, Does.EndWith("      value: Old.Assembly Old.Namespace.Parent/OldType\n      objectReference: {fileID: 0}\n"));
            Assert.That(rewritten, Does.Contain("propertyPath: 'managedReferences[42]'"));
        }

        [Test]
        public void Apply_ChangedSincePreview_RejectsStalePlan()
        {
            var path = "Assets/__ManagedReferenceMigrationStale_" + Guid.NewGuid().ToString("N") + ".asset";
            try
            {
                File.WriteAllText(path, "changed");
                var plan = new ManagedReferenceMigrationPlan();
                plan.changes.Add(new ManagedReferenceAssetChange { path = path, original = Encoding.UTF8.GetBytes("before"), migrated = Encoding.UTF8.GetBytes("after") });
                Assert.That(() => ManagedReferenceMigration.Apply(plan), Throws.InvalidOperationException.With.Message.Contains("changed since"));
                Assert.That(File.ReadAllText(path), Is.EqualTo("changed"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Apply_MissingSerializedType_RestoresSharedNestedAndUnityObjectReferences()
        {
            var folder = "Assets/__ManagedReferenceMigration_" + Guid.NewGuid().ToString("N");
            var guid = AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            var path = folder + "/Host.asset";
            var targetPath = folder + "/Target.asset";
            var current = ManagedReferenceTypeIdentity.FromType(typeof(MigrationNode));
            var previous = Identity("Before");
            var backupMarker = "Library/ManagedReferencePicker/Backups/latest.txt";
            var previousBackup = File.Exists(backupMarker) ? File.ReadAllText(backupMarker) : null;
            string backupDirectory = null;
            try
            {
                var target = ScriptableObject.CreateInstance<MigrationAssetHost>();
                AssetDatabase.CreateAsset(target, targetPath);
                var host = ScriptableObject.CreateInstance<MigrationAssetHost>();
                host.primary = new MigrationNode { value = 17, text = "preserved", target = target, next = new MigrationNode { value = 23 } };
                host.shared = host.primary;
                host.nodes.Add(host.primary.next);
                AssetDatabase.CreateAsset(host, path);
                AssetDatabase.SaveAssetIfDirty(host);
                AssetDatabase.SaveAssetIfDirty(target);
                var original = File.ReadAllText(path);
                var broken = ManagedReferenceYaml.Rewrite(original,
                    new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> { [current] = previous }, out var count);
                Assert.That(count, Is.EqualTo(2));
                File.WriteAllText(path, broken, new UTF8Encoding(true));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Assert.That(SerializationUtility.HasManagedReferencesWithMissingTypes(host), Is.True);
                var registry = new ManagedReferenceTypeRegistry();
                registry.entries.Add(new ManagedReferenceTypeEntry { current = current, history = new List<ManagedReferenceTypeIdentity> { previous } });
                var plan = ManagedReferenceMigration.Scan(registry, new[] { path });
                Assert.That(plan.issues, Is.Empty);
                Assert.That(plan.changes, Has.Count.EqualTo(1));
                backupDirectory = ManagedReferenceMigration.Apply(plan);
                host = AssetDatabase.LoadAssetAtPath<MigrationAssetHost>(path);
                Assert.That(SerializationUtility.HasManagedReferencesWithMissingTypes(host), Is.False);
                Assert.That(host.primary.value, Is.EqualTo(17));
                Assert.That(host.primary.text, Is.EqualTo("preserved"));
                Assert.That(host.shared, Is.SameAs(host.primary));
                Assert.That(host.nodes[0], Is.SameAs(host.primary.next));
                Assert.That(host.primary.next.value, Is.EqualTo(23));
                Assert.That(host.primary.target, Is.EqualTo(target));
                Assert.That(File.ReadAllBytes(path).Take(3), Is.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
                var migratedBytes = File.ReadAllBytes(path);
                var changedBytes = migratedBytes.Concat(Encoding.UTF8.GetBytes("\n# Changed after migration\n")).ToArray();
                File.WriteAllBytes(path, changedBytes);
                Assert.That(() => ManagedReferenceMigration.RestoreLastBackup(), Throws.InvalidOperationException.With.Message.Contains("changed since"));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(changedBytes));
                File.WriteAllBytes(path, migratedBytes);
                ManagedReferenceMigration.RestoreLastBackup();
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(plan.changes[0].original));
            }
            finally
            {
                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));
                RestoreTestBackup(backupMarker, previousBackup, backupDirectory);
            }
        }

        [Test]
        public void Apply_OpenScene_ReopensSceneWithPreservedData()
        {
            var folder = "Assets/__ManagedReferenceSceneMigration_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            var path = folder + "/Host.unity";
            var current = ManagedReferenceTypeIdentity.FromType(typeof(MigrationNode));
            var previous = Identity("Before");
            var backupMarker = "Library/ManagedReferencePicker/Backups/latest.txt";
            var previousBackup = File.Exists(backupMarker) ? File.ReadAllText(backupMarker) : null;
            string backupDirectory = null;
            try
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var root = new GameObject("SceneMigrationHost");
                root.AddComponent<MigrationPrefabHost>().node = new MigrationNode { value = 71, text = "scene data" };
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
                var broken = ManagedReferenceYaml.Rewrite(File.ReadAllText(path),
                    new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> { [current] = previous }, out var count);
                Assert.That(count, Is.EqualTo(1));
                File.WriteAllText(path, broken);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var registry = new ManagedReferenceTypeRegistry();
                registry.entries.Add(new ManagedReferenceTypeEntry { current = current, history = new List<ManagedReferenceTypeIdentity> { previous } });
                backupDirectory = ManagedReferenceMigration.Apply(ManagedReferenceMigration.Scan(registry, new[] { path }));
                var restoredScene = SceneManager.GetActiveScene();
                Assert.That(restoredScene.path, Is.EqualTo(path));
                Assert.That(restoredScene.isDirty, Is.False);
                var host = restoredScene.GetRootGameObjects().Single().GetComponent<MigrationPrefabHost>();
                Assert.That(host.node.value, Is.EqualTo(71));
                Assert.That(host.node.text, Is.EqualTo("scene data"));
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(folder);
                RestoreTestBackup(backupMarker, previousBackup, backupDirectory);
            }
        }

        [Test]
        public void Apply_PrefabAndVariant_PreservesOverrideValues()
        {
            var folder = "Assets/__ManagedReferencePrefabMigration_" + Guid.NewGuid().ToString("N");
            var folderGuid = AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            var basePath = folder + "/Base.prefab";
            var variantPath = folder + "/Variant.prefab";
            var temporaryScene = default(Scene);
            var current = ManagedReferenceTypeIdentity.FromType(typeof(MigrationNode));
            var previous = Identity("Before");
            var backupMarker = "Library/ManagedReferencePicker/Backups/latest.txt";
            var previousBackup = File.Exists(backupMarker) ? File.ReadAllText(backupMarker) : null;
            string backupDirectory = null;
            try
            {
                temporaryScene = EditorSceneManager.NewPreviewScene();
                var root = new GameObject("MigrationHost");
                SceneManager.MoveGameObjectToScene(root, temporaryScene);
                root.AddComponent<MigrationPrefabHost>().node = new MigrationNode { value = 11 };
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, basePath);
                UnityEngine.Object.DestroyImmediate(root);
                var variant = (GameObject)PrefabUtility.InstantiatePrefab(prefab, temporaryScene);
                var component = variant.GetComponent<MigrationPrefabHost>();
                component.node = new MigrationNode { value = 29, text = "variant override" };
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                PrefabUtility.SaveAsPrefabAsset(variant, variantPath);
                UnityEngine.Object.DestroyImmediate(variant);
                EditorSceneManager.ClosePreviewScene(temporaryScene);
                foreach (var path in new[] { basePath, variantPath })
                {
                    var broken = ManagedReferenceYaml.Rewrite(File.ReadAllText(path),
                        new Dictionary<ManagedReferenceTypeIdentity, ManagedReferenceTypeIdentity> { [current] = previous }, out var count);
                    Assert.That(count, Is.EqualTo(1), path);
                    File.WriteAllText(path, broken);
                    if (path == variantPath)
                    {
                        LogAssert.Expect(LogType.Error, new Regex("Could not update a managed instance value at property path 'managedReferences\\[.*\\]', with value 'Old.Assembly Old.Namespace.Before'"));
                        LogAssert.Expect(LogType.Error, new Regex("The serialized array of \\[SerializeReference\\] objects is missing entry for Refid \\d+"));
                    }

                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                }

                var registry = new ManagedReferenceTypeRegistry();
                registry.entries.Add(new ManagedReferenceTypeEntry { current = current, history = new List<ManagedReferenceTypeIdentity> { previous } });
                var plan = ManagedReferenceMigration.Scan(registry, new[] { basePath, variantPath });
                Assert.That(plan.changes, Has.Count.EqualTo(2));
                backupDirectory = ManagedReferenceMigration.Apply(plan);
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(basePath).GetComponent<MigrationPrefabHost>().node.value, Is.EqualTo(11));
                var restoredVariant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
                Assert.That(PrefabUtility.GetPrefabAssetType(restoredVariant), Is.EqualTo(PrefabAssetType.Variant));
                Assert.That(restoredVariant.GetComponent<MigrationPrefabHost>().node.value, Is.EqualTo(29));
                Assert.That(restoredVariant.GetComponent<MigrationPrefabHost>().node.text, Is.EqualTo("variant override"));
            }
            finally
            {
                if (temporaryScene.IsValid() && temporaryScene.isLoaded)
                {
                    EditorSceneManager.ClosePreviewScene(temporaryScene);
                }

                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(folderGuid));
                RestoreTestBackup(backupMarker, previousBackup, backupDirectory);
            }
        }

        private static void RestoreTestBackup(string marker, string previous, string directory)
        {
            if (previous == null)
            {
                if (File.Exists(marker))
                {
                    File.Delete(marker);
                }
            }
            else
            {
                File.WriteAllText(marker, previous);
            }

            if (directory != null)
            {
                var root = Path.GetFullPath("Library/ManagedReferencePicker/Backups") + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(directory);
                Assert.That(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), Is.True);
                Directory.Delete(path, true);
            }
        }
    }
}
