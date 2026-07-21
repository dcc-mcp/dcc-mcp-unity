from typing import List, Optional, Union

from dcc_mcp_core.skill import skill_entry, skill_success

from dcc_mcp_unity.bridge import call_host


@skill_entry
def main(
    instance_id: Union[int, str],
    position: Optional[List[float]] = None,
    rotation_euler: Optional[List[float]] = None,
    scale: Optional[List[float]] = None,
    **_kwargs,
):
    params = {"instance_id": instance_id}
    for name, value in (
        ("position", position),
        ("rotation_euler", rotation_euler),
        ("scale", scale),
    ):
        if value is not None:
            params[name] = value
    result = call_host("scene.set_transform", params)
    return skill_success(f"Updated Unity transform {instance_id}.", **result)


if __name__ == "__main__":
    from dcc_mcp_core.skill import run_main

    run_main(main)
