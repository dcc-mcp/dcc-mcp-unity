from typing import Union

from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(name: str, parent_instance_id: Union[int, str] = 0, **_kwargs):
    result = call_host(
        "scene.create_game_object",
        {"name": name, "parent_instance_id": parent_instance_id},
    )
    return skill_success(f"Created Unity GameObject {name}.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
