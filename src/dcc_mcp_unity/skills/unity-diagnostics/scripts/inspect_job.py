from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(request_id: str, **_kwargs):
    result = call_host("jobs.inspect", {"request_id": request_id})
    return skill_success("Unity job inspected.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
