from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(component_id, expected_fingerprint=None, **_kwargs):
    params = {
        "component_id": component_id,
        "expected_fingerprint": expected_fingerprint,
    }
    result = call_host("components.remove", {k: v for k, v in params.items() if v is not None})
    return skill_success("Unity component operation completed.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
