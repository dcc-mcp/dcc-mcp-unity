from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(max_nodes: int = 1000, **_kwargs):
    result = call_host("scene.inspect", {"max_nodes": max_nodes})
    return skill_success("Unity scene inspected.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
