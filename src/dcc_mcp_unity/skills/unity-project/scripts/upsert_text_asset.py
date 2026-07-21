from dcc_mcp_core.skill import skill_entry, skill_error

from dcc_mcp_unity.bridge import call_host
from dcc_mcp_unity.job_result import job_state_result


@skill_entry
def main(request_id: str, path: str, content: str, expected_sha256: str, **_kwargs):
    if any(
        (ord(character) < 0x20 and character not in "\t\n\r") or character == "\x7f"
        for character in content
    ):
        return skill_error(
            "Unity text asset write was rejected before transport.",
            "content cannot contain JSON-unsafe control characters.",
        )
    if len(content.encode("utf-8")) > 256 * 1024:
        return skill_error(
            "Unity text asset write was rejected before transport.",
            "content must be at most 256 KiB when encoded as UTF-8.",
        )
    result = call_host(
        "assets.upsert_text",
        {
            "request_id": request_id,
            "path": path,
            "content": content,
            "expected_sha256": expected_sha256,
        },
    )
    return job_state_result("Unity text asset write", result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
