from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(root=None, query=None, limit=None, **_kwargs):
    params = {"root": root, "query": query, "limit": limit}
    result = call_host("assets.find", {k: v for k, v in params.items() if v is not None})
    return skill_success("Unity asset reuse operation completed.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
