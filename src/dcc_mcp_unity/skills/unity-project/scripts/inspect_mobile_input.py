from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.mobile_input import inspect_mobile_input


@skill_entry
def main(path: str, target_platform: str = "android", **_kwargs):
    return skill_success("Mobile input classified.", **inspect_mobile_input(path, target_platform))


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
