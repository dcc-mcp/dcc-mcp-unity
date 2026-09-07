from dcc_mcp_core.skill import skill_entry, skill_error, skill_success

from dcc_mcp_unity.android_device import execute


@skill_entry
def main(request_id, build_request_id, device_id, marker=None, timeout_seconds=10, **_kwargs):
    result = execute(
        "logs",
        request_id=request_id,
        build_request_id=build_request_id,
        device_id=device_id,
        marker=marker,
        timeout_seconds=timeout_seconds,
    )
    if result["state"] != "succeeded":
        return skill_error(result["message"], device_request=result)
    return skill_success("Android device operation completed.", device_request=result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
