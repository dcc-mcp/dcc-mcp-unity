from typing import Union

from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(instance_id: Union[int, str], **_kwargs):
    return skill_success(
        "Unity GameObject inspected.",
        **call_host("scene.inspect_game_object", {"instance_id": instance_id}),
    )


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
