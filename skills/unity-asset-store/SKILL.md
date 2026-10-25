---
name: unity-asset-store
description: >-
  Plan how to acquire Unity Asset Store resources from an official product link,
  including Mech Combat Kit, then verify assets after the user downloads and imports
  them through Unity. Use for Asset Store packages, templates, and resource downloads.
  This skill does not download packages or verify account entitlement.
license: MIT
compatibility: "Python 3.9+; dcc-mcp-core 0.19.90+; Unity adapter for post-import checks"
allowed-tools: "python"
metadata:
  dcc-mcp:
    dcc: unity
    layer: domain
    version: "0.1.0"
    search-hint: "Unity Asset Store resource download acquire import unitypackage Mech Combat Kit 资源 下载 商城"
    tags: "unity,asset-store,assets,templates,resources"
    tools: tools.yaml
---

# Unity Asset Store

This optional marketplace skill turns a user-supplied official product URL into a
local acquisition plan. It makes no network requests and changes no files or Unity
state. Install it separately from the adapter's bundled project and scene skills.

## Acquisition workflow

1. Call `plan_asset_acquisition` with the supplied product URL. Use the returned
   `asset_id` and tracking-free `product_url` when identifying the requested package.
   A successful plan means only that the link was recognized. The tool does not
   retrieve price, package format, publisher, version, compatibility, or entitlement.
2. Have the user check the live listing's publisher, supported Unity version, render
   pipelines, dependencies, license, and price. The user signs in and purchases or
   adds the resource to My Assets through Unity's official interface. Do not infer
   ownership from a public listing or from a successful plan.
3. Before import, use `unity-project` → `inspect_project` to confirm the intended
   project, Editor version, and readiness. Save user work and make a project backup
   or version-control checkpoint before importing third-party code or settings.
4. The user opens Unity's Package Manager → My Assets, selects the acquired package,
   and downloads it. For `.unitypackage` assets, the user reviews the import selection
   and any existing-file or project-settings changes before importing. UPM assets
   use the official Package Manager installation flow instead. A product URL cannot
   establish which format is available.
5. After the user reports import complete, wait for compilation and asset updates
   to finish. Use `inspect_project`, `search_assets` with an expected name supplied
   by the user or package documentation, and `unity-diagnostics` → `read_console`.
   Record the actual imported paths and errors. Search results prove presence of
   those assets only; require a package-specific scene or test before claiming the
   kit works. `refresh_assets` discovers changed files; it does not unpack a
   `.unitypackage`.

The Asset Store's [Terms, section 3.3](https://unity.com/legal/as-terms) restrict
automated access unless separately permitted by Unity. Keep acquisition with the
user's official flow. Do not scrape storefronts, use private download APIs, collect
Unity credentials or session tokens, mirror packages, or put vendor content in a
skill. This package distributes only the workflow and link planner. The skill's MIT
license does not grant rights to the third-party resource.

For an authorized local Unity UI check, use the project's `dcc-cua` / DCC-MCP
`ui-control` route. Before any observation or input, report `provider=dcc-cua`, the
runtime version, target PID, and HWND. Missing binding or route means stop UI work
and report the blocker; never fall back to generic browser or computer automation.
UI routing does not grant permission to automate Asset Store acquisition.

## Example

```json
{"asset_url":"https://assetstore.unity.com/packages/templates/systems/mech-combat-kit-164854"}
```

Use Mech Combat Kit as a product-link example, not as a bundled dependency. Do not
promise compatibility with every Editor supported by the DCC-MCP Unity adapter.

## Recovery

An invalid URL requires a complete HTTPS product link on `assetstore.unity.com`.
If the package is missing from My Assets, the user checks the signed-in account and
purchase or acquisition with Unity support. Do not retry with another downloader.
The planner needs no rollback because it has no side effects. For a failed later
manual import, retain Console errors and restore the pre-import project checkpoint
before retrying; deleting a package folder can leave changed settings or dependencies.

Official references: [download and import asset packages](https://docs.unity3d.com/Manual/upm-ui-import.html),
[install Asset Store UPM packages](https://docs.unity3d.com/Manual/upm-ui-install2.html),
and [Asset Store Terms and EULA](https://unity.com/legal/as-terms).
