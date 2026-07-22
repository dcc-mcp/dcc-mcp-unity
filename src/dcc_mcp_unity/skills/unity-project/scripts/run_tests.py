from dcc_mcp_core.skill import skill_entry

from dcc_mcp_unity.bridge import call_host
from dcc_mcp_unity.job_result import job_state_result


@skill_entry
def main(request_id: str, test_mode: str, test_names=None, **_kwargs):
    parameters = {
        "request_id": request_id,
        "test_mode": test_mode,
        "test_names": list(test_names or []),
    }
    result = call_host("project.run_tests", parameters)
    return job_state_result("Unity Test Runner", result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
