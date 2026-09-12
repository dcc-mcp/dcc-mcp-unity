from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(instance_id, **_kwargs):
    params = {
        "instance_id": instance_id,
    }
    result = call_host("components.list", {k: v for k, v in params.items() if v is not None})
    return skill_success("Unity component operation completed.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
