from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(path=None, scene_handle=None, expected_dependency_hash=None, **_kwargs):
    params = {
        "path": path,
        "scene_handle": scene_handle,
        "expected_dependency_hash": expected_dependency_hash,
    }
    result = call_host(
        "scene.instantiate_prefab", {k: v for k, v in params.items() if v is not None}
    )
    return skill_success("Unity asset reuse operation completed.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
