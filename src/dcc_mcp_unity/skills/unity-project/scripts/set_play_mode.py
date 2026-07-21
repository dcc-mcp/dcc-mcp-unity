from dcc_mcp_core.skill import skill_entry

from dcc_mcp_unity.bridge import call_host
from dcc_mcp_unity.job_result import job_state_result


@skill_entry
def main(request_id: str, play: bool, **_kwargs):
    result = call_host("editor.set_play_mode", {"request_id": request_id, "play": play})
    return job_state_result("Unity Play Mode", result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
