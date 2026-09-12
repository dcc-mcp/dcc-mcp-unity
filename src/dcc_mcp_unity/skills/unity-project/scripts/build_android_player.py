from dcc_mcp_core.skill import skill_entry

from dcc_mcp_unity.bridge import call_host
from dcc_mcp_unity.job_result import job_state_result


@skill_entry
def main(request_id: str, artifact_kind: str, **_kwargs):
    result = call_host(
        "project.build_android_player",
        {"request_id": request_id, "artifact_kind": artifact_kind},
    )
    return job_state_result("Unity Android player build", result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
