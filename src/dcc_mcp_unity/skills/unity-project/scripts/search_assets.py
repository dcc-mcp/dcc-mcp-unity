from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(query: str, max_results: int = 100, **_kwargs):
    return skill_success(
        "Unity assets searched.",
        **call_host("assets.search", {"query": query, "max_results": max_results}),
    )


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
