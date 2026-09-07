from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.android_device import enumerate_devices


@skill_entry
def main(**_kwargs):
    return skill_success("Android device discovery completed.", **enumerate_devices())


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
