from typing import Any

from dcc_mcp_core.skill import skill_error, skill_success


def job_state_result(action: str, result: dict[str, Any]):
    state = result.get("state")
    if state not in {"queued", "running", "succeeded", "failed"}:
        context = dict(result)
        error = str(context.pop("error", repr(state)))
        context["returned_state"] = context.pop("state", None)
        return skill_error(
            f"{action} returned an invalid job state.",
            error,
            **context,
        )
    message = f"{action} job state returned: {state}."
    if state == "failed":
        context = dict(result)
        error = str(context.pop("error", "Unity job failed without an error message."))
        return skill_error(message, error, **context)
    return skill_success(message, **result)
