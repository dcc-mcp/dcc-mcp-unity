from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(
    path: str,
    pixels_per_unit: int = 100,
    filter_mode: str = "bilinear",
    **_kwargs,
):
    result = call_host(
        "assets.configure_sprite",
        {
            "path": path,
            "pixels_per_unit": pixels_per_unit,
            "filter_mode": filter_mode,
        },
    )
    return skill_success("Unity sprite importer configured.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
