from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(tool_name: str, parameters=None, **_kwargs):
    result = call_host(
        "tuanjie_ai.execute",
        {"tool_name": tool_name, "parameters": parameters or {}},
    )
    return skill_success("Tuanjie AI native tool executed.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
