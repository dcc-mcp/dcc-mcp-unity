from dcc_mcp_core.skill import skill_entry

from dcc_mcp_unity.bridge import call_host
from dcc_mcp_unity.job_result import job_state_result


@skill_entry
def main(request_id: str, **_kwargs):
    result = call_host("editor.capture_game_view", {"request_id": request_id})
    return job_state_result("Unity Game View capture", result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
