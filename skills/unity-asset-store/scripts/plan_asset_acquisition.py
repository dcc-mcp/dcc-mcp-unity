import re
from urllib.parse import urlsplit

from dcc_mcp_core.skill import skill_entry, skill_success


@skill_entry
def main(asset_url: str, **_kwargs):
    if (
        not isinstance(asset_url, str)
        or not 1 <= len(asset_url) <= 2048
        or any(ord(char) < 33 or 127 <= ord(char) <= 159 or char.isspace() for char in asset_url)
        or "\\" in asset_url
    ):
        raise ValueError(
            "Provide an HTTPS Unity Asset Store product URL of at most 2048 characters."
        )
    parsed = urlsplit(asset_url)
    product = re.fullmatch(
        r"/packages/(?:[a-z0-9]+(?:-[a-z0-9]+)*/)+"
        r"[a-z0-9]+(?:-[a-z0-9]+)*-([1-9][0-9]*)/?",
        parsed.path,
    )
    if parsed.scheme != "https" or parsed.netloc.lower() != "assetstore.unity.com" or not product:
        raise ValueError("Use a canonical https://assetstore.unity.com/packages/.../name-ID URL.")

    return skill_success(
        "Asset Store acquisition plan prepared; no package was downloaded or imported.",
        asset_id=product.group(1),
        product_url="https://assetstore.unity.com" + parsed.path.rstrip("/"),
        acquisition_state="requires_official_acquisition",
        download_supported=False,
        import_supported=False,
        acquisition_steps=[
            "Open the official product page yourself and acquire the asset with an authorized "
            "Unity account; review its license and compatibility first.",
            "In the intended Unity project, open Package Manager > My Assets, select the asset, "
            "and use Unity's official Download action.",
            "Review the package contents and project changes before using Unity's Import action.",
            "After import and compilation finish, use unity-project inspect_project/search_assets "
            "and unity-diagnostics read_console to verify the intended project and imports.",
        ],
    )


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
