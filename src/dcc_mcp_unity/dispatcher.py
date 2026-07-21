"""In-process skill dispatcher for the external Unity Editor bridge."""

from __future__ import annotations

from typing import Any, Callable


class UnityBridgeDispatcher:
    """Run Python wrappers inline; Unity performs host work on its editor update loop."""

    def dispatch_callable(self, func: Callable[..., Any], *args: Any, **kwargs: Any) -> Any:
        for key in (
            "affinity",
            "context",
            "action_name",
            "skill_name",
            "execution",
            "timeout_hint_secs",
        ):
            kwargs.pop(key, None)
        return func(*args, **kwargs)
