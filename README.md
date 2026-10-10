# Managed Reference Picker

A one-attribute way to create polymorphic instances directly on a field, without a dedicated
builder object.

Unity's `[SerializeReference]` can store polymorphic data in a field/array/list, but the inspector
gives you no way to pick a concrete subclass — new elements are just `null`. The usual workaround is
to build a whole owning `MonoBehaviour`/`ScriptableObject` (plus a custom editor) around the list
just to add a type picker. `[ManagedReferencePicker]` collapses that into a single field
attribute.

## Usage

Mark any polymorphic field with **both** `[SerializeReference]` and `[ManagedReferencePicker]`.
Works on a single reference, an array, or a `List<T>`:

```csharp
using System;
using System.Collections.Generic;
using UnityEngine;
using ManagedReferencePicker;

public interface IAbilityBlock { }

[Serializable]
public class DamageOverTimeBlock : IAbilityBlock
{
    [SerializeField] private float damagePerSecond = 5f;
}

[Serializable]
public class KnockbackBlock : IAbilityBlock
{
    [SerializeField] private float force = 10f;
}

public class Ability : MonoBehaviour
{
    // Single instance
    [SerializeReference, ManagedReferencePicker]
    private IAbilityBlock primary;

    // A list — Unity applies the drawer per element, so each element gets its own type picker.
    [SerializeReference, ManagedReferencePicker]
    private List<IAbilityBlock> blocks = new();
}
```

In the inspector each slot shows a **type dropdown**. Picking a type instantiates that concrete
subclass in place; `(None)` clears it. Add/remove/reorder come free from Unity's built-in list UI.

### Forbidding duplicates

Pass `false` to the constructor to stop a list from holding two instances of the same concrete
type — types already used on another element are greyed out in the dropdown:

```csharp
[SerializeReference, ManagedReferencePicker(allowDuplicates: false)]
private List<IAbilityBlock> blocks = new();
```

Defaults to `true`. Has no effect on a single (non-collection) field.

## Requirements for offered types

A type appears in the dropdown when it is:

* a non-abstract, non-generic **class** (not a `UnityEngine.Object`), and
* has a public parameterless constructor, and
* is assignable to the field's element type.

## Relationship to `BuildingBlocks`

`BuildingBlocks` bundles this instance-creation UI together with storage, lookup, and duplicate
policy on a base builder class. Reach for `[ManagedReferencePicker]` when you only need "let me
pick and create instances on this field" and don't want to inherit a builder type. It has no
dependency on `BuildingBlocks` and can be used entirely on its own.

## Refactoring managed reference types

The editor tracks serializable concrete classes under `Assets` using their script `.meta` GUIDs.
No type ID attributes or changes to `[SerializeReference]` fields are needed. Type history is
stored in `ProjectSettings/ManagedReferencePickerTypes.json`; commit it with the scripts and
their `.meta` files. The registry is initialized after compilation, so install the plugin and
let it record your types **before** refactoring them.

After renaming a class, changing its namespace, or moving it between assemblies:

1. Wait for compilation to finish.
2. Open **Tools > Managed Reference Picker > Migrations** and click **Scan**.
3. Review the type mappings and affected assets. For an ambiguous rename, choose a replacement
   under **Associate missing types**, then click **Associate and preview**.
4. Save or discard unsaved scenes and affected assets, close Prefab mode, and click **Apply preview**.

Namespace and assembly changes are matched by class name within the same script GUID. A class
rename is inferred only when its file had and still has one tracked class. Renames in files with
multiple tracked classes, or a rename combined with moving the class into another file, require
an explicit association. A matching identity moved to another file updates its tracked GUID.
The tool retains old names so assets arriving later from another branch can also be migrated.

Migration changes only the type metadata in Unity text assets with managed reference format
version 2, including managed reference type records in prefab overrides. Reference IDs, field
payloads, shared references, and Unity object references are
preserved. The tool handles `.asset`, `.prefab`, `.unity`, `.controller`, `.overrideController`,
and `.playable` files under `Assets`. Keep **Asset Serialization > Mode** set to **Force Text**;
binary assets are reported and skipped. Already missing types without registry history can be
associated manually in the window.

The preview is rejected if any affected file has changed since scanning. Each migration backs
up the original bytes under `Library/ManagedReferencePicker/Backups`. **Restore last migration**
restores the last successful migration only if those assets have not changed since applying it.
Backups are local and disappear when `Library` is deleted; use source control for permanent history.
Affected open scenes are temporarily closed and reopened during migration.

This is editor-only tooling. Player builds use Unity's normal serialization with no registry,
GUID lookup, or runtime overhead. A build check rejects pending migrations and missing types
found in supported text assets. Binary assets and unsupported reference formats are outside
that check. External saves and already-built AssetBundles are not migrated.

This repairs type identity changes, not changes to a type's field layout or behavior. Use Unity's
`[FormerlySerializedAs]` for field renames. Source discovery supports ordinary block/file-scoped
namespaces, multiple classes, and nested classes; generic and multi-file partial types are not
tracked. Types supplied only in DLLs need explicit migration metadata such as `[MovedFrom]`.

## Tests

The `ManagedReferencePicker.Editor.Tests` assembly contains Edit Mode regression tests for
registry history, ambiguous mappings, source discovery, targeted YAML migration, stale previews,
backup restoration, shared/nested references, Unity object references, open scenes, and prefab variants.

## Installation

Add to `Packages/manifest.json`:

```json
"com.twinkocat.managed-reference-picker": "<git url of this repo>"
```
