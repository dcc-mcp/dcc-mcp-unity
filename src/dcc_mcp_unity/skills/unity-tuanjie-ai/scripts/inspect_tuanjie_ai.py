from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(**_kwargs):
    return skill_success("Tuanjie AI capabilities inspected.", **call_host("tuanjie_ai.inspect"))


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
