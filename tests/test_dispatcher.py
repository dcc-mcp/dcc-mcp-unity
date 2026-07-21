from dcc_mcp_unity.dispatcher import UnityBridgeDispatcher


def test_dispatcher_removes_core_metadata():
    dispatcher = UnityBridgeDispatcher()
    assert (
        dispatcher.dispatch_callable(
            lambda value=7: value,
            affinity="main",
            action_name="test",
            timeout_hint_secs=5,
        )
        == 7
    )
