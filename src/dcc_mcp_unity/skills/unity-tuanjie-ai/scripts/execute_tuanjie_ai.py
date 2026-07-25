from dcc_mcp_core.skill import skill_entry, skill_error, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(tool_name: str, parameters=None, **_kwargs):
    result = call_host(
        "tuanjie_ai.execute",
        {"tool_name": tool_name, "parameters": parameters or {}},
    )
    if result.get("success") is False:
        return skill_error(
            "Tuanjie AI native tool failed.",
            str(result.get("message") or "Native tool reported failure."),
            native_result=result,
        )
    return skill_success("Tuanjie AI native tool executed.", native_result=result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
