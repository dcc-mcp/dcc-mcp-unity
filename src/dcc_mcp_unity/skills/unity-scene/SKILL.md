---
name: unity-scene
description: >-
  Domain skill — Inspect and edit the active Unity scene with typed, undoable
  GameObject operations. Use for hierarchy, object creation, transforms, and
  scene saves. Not for asset imports — use unity-project.
license: MIT
compatibility: "Unity 2018.4.25f1+ (.NET 4.x); dcc-mcp-core 0.19.90+"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.16.0"  # x-release-please-version
    search-hint: "Unity scene hierarchy GameObject transform save Undo prefab assets dependencies"
    tags: "unity,scene,gameobject,transform,game-development"
    tools: tools.yaml
    depends: "dcc-diagnostics"
---

# Unity Scene

Inspect the hierarchy immediately before using instance IDs. Treat every Unity instance ID as an
opaque, session-scoped value and return it unchanged; Unity 6000.5+ emits decimal strings while
older Editors retain integer output for compatibility. Both forms are accepted as input. Object
creation and transform edits register with Unity Undo.

Do not automatically retry a timed-out mutation. Inspect the project and scene first because the
Editor may have completed the original request near the timeout boundary.

## Component authoring

Call `list_components` with a fresh hierarchy ID. Its `object_id` and `component_id`
handles expire on domain reload or object destruction. Add by fully qualified type
name; inspect the component before setting exact serialized property paths. Supply
`expected_fingerprint` to reject concurrent edits. Values support booleans, integers,
floats, strings, enum indices, vectors, colors, rectangles, bounds, and compatible
object references (`object_id` or an exact `Assets/` path). Existing nested fields
and array elements use Unity property paths; array resizing, curves, managed
references, event wiring, and direct prefab asset edits are unsupported in this
iteration. Unsupported fields are marked non-editable.

Batches validate in a SerializedObject before applying and use the command Undo
transaction for failures. Project component lifecycle callbacks can have their own
external side effects; Undo cannot roll those back. Results include normalized
readback and scene/prefab override state. No operation saves content. Review dirty
scenes before the existing `save_scene`, which saves all open scenes.

## Reuse local assets

Use `find_assets` within Assets or installed Packages, then `inspect_asset` for
identity and recursive dependency paths. Results are bounded and indicate truncation.
Project and UPM assets are distinguished; their presence does not prove licensing.
These tools do not search or purchase Asset Store products, install packages, import
archives, execute downloaded code, or generate visual previews.

Save any untitled scene to an explicit path first; Unity cannot add scenes beside it.
For a separate layout, call `create_isolated_scene`, then `instantiate_prefab` with
its scene handle and the asset's current dependency hash. Use returned object IDs
with `set_transform` to arrange instances. Save only that scene with `save_exact_scene`
to an existing Assets folder. `reopen_exact_scene` refuses dirty scenes and reads the
saved scene from disk; use its new handle and fresh hierarchy IDs afterward.
Keep another scene loaded during reopen. Never automatically retry mutations.

Scene handles are opaque decimal strings, scoped to the current Editor session.
Pass them through unchanged, including on Unity 6.5 where they can exceed safe JSON
integer precision. Legacy 32-bit integer input remains accepted. Reinspect after
reopening; a handle must never be replaced with the active scene when it is stale.

Exact scene saving requires the complete dirty-asset snapshot implementation. Unity's
native scene-save pipeline can also flush unrelated dirty persistent assets, so
`save_exact_scene` takes a fresh snapshot synchronously and classifies every record.
Dirty user assets, unknown identities/scopes, changed descriptors, field errors,
unsupported capture, or incomplete/count-inconsistent state refuse before saving.
It never saves assets to make the check pass, restores files, or re-marks objects dirty.

The snapshot also contains ordinary Unity-internal dirtiness. The bounded exceptions
are verified MonoScript/Shader/Texture2D objects at Unity's three exact built-in resource
paths; identity-complete MonoScript objects from a confirmed immutable package source
that Unity reports noneditable; and the exact native UnityEditor.SceneVisibilityState
type. Local/Embedded/unknown packages and dirty package Materials or other user assets
remain protected. Object ID, actual runtime type/assembly, and asset path are rechecked
without loading assets. A package prefix or hide flag alone never permits saving.

SceneVisibilityState is an explicit editor-local-state boundary: Unity's save callbacks
may update `Library/SceneVisibilityState.asset`. It is not classified as unwritable.
The result reports the actual exception counts and that possible editor-state write.
Unrelated scene visibility/picking state and project asset bytes are native regression
targets; zero writes to Unity's internal cache are not promised. The raw snapshot may
be nonzero and is never relabeled clean.
Unity 2018.4 has no supported public dirty query and therefore refuses exact saves;
do not fall back to `save_scene` automatically. Other scene operations remain available.

The preflight covers currently loaded persistent objects only. It is not a guarantee
against external writers or project callbacks that create or save new dirty state
during native saving. Nested DCC commands are rejected during the exact-save call.
Use a clean, disposable project with reviewed local content for isolated acceptance.

Boundary references: [Unity scene visibility](https://docs.unity3d.com/2021.3/Documentation/Manual/SceneVisibility.html),
[package mutability](https://docs.unity3d.com/2021.3/Documentation/Manual/upm-concepts.html#Management),
and Unity's [version-pinned built-in path classification](https://github.com/Unity-Technologies/UnityCsReference/blob/b052b1fe38b45d5aefa609a115c626ed2e74f3a6/Modules/QuickSearch/Editor/Utilities/Utils.cs#L1279-L1290).
The implementation is independently authored; these references establish semantics.
