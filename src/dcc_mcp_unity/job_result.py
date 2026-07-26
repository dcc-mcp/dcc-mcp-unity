from typing import Any

from dcc_mcp_core import DeferredToolResult
from dcc_mcp_core.bridge import BridgeConnectionError, BridgeRpcError, BridgeTimeoutError
from dcc_mcp_core.skill import skill_error, skill_success

from dcc_mcp_unity.bridge import call_host


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
    if state in {"queued", "running"}:
        request_id = result.get("request_id")

        def check_is_finished():
            try:
                snapshot = call_host("jobs.inspect", {"request_id": request_id})
            except (BridgeConnectionError, BridgeTimeoutError):
                return None
            except BridgeRpcError as exception:
                if (
                    exception.code == -32000
                    and exception.message == f"Unity job was not found for request_id: {request_id}"
                ):
                    return None
                raise
            if snapshot.get("state") in {"queued", "running"}:
                return None
            return job_state_result(action, snapshot)

        return DeferredToolResult(
            check_is_finished=check_is_finished,
            timeout_secs=3600,
            poll_interval_secs=0.25,
        )
    message = f"{action} job state returned: {state}."
    if state == "failed":
        context = dict(result)
        error = str(context.pop("error", "Unity job failed without an error message."))
        return skill_error(message, error, **context)
    return skill_success(message, **result)
