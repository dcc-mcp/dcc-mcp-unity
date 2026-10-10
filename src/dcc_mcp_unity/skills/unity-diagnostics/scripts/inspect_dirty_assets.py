from dcc_mcp_core.skill import skill_entry, skill_error, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(**kwargs):
    if kwargs:
        return skill_error("inspect_dirty_assets accepts no parameters.", "invalid_parameters")
    result = call_host("editor.inspect_dirty_assets", {})
    if not isinstance(result, dict):
        return skill_error("Unity dirty snapshot is invalid.", "dirty_snapshot_invalid")
    if result.get("complete") is not True or result.get("incomplete") is not False:
        return skill_error(
            result.get("error_message") or "Unity dirty snapshot is incomplete.",
            result.get("error_code") or "dirty_snapshot_incomplete",
            **result,
        )
    count = result.get("dirty_persistent_count")
    items = result.get("items")
    owner = result.get("owner")
    source = result.get("source")
    if (
        type(count) is not int
        or count < 0
        or type(result.get("items_count")) is not int
        or result["items_count"] != count
        or not isinstance(items, list)
        or len(items) != count
        or type(result.get("schema_version")) is not int
        or result["schema_version"] != 1
        or not isinstance(result.get("capture_id"), str)
        or len(result["capture_id"]) != 32
        or not isinstance(owner, dict)
        or type(owner.get("process_id")) is not int
        or owner["process_id"] <= 0
        or not all(
            isinstance(owner.get(key), str) and owner[key]
            for key in ("project_path", "engine_version", "session_instance_id")
        )
        or not isinstance(source, dict)
        or not all(
            isinstance(source.get(key), str) and source[key]
            for key in ("assembly_full_name", "module_version_id")
        )
        or result.get("error_code") is not None
        or result.get("error_message") is not None
    ):
        return skill_error(
            "Unity dirty snapshot counts or identity are invalid.", "dirty_snapshot_invalid"
        )
    return skill_success("Unity loaded persistent dirty assets inspected.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
