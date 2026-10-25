import importlib.util
import socket
from pathlib import Path

import pytest
import yaml
from jsonschema import Draft7Validator

SKILL = Path(__file__).parents[1] / "skills" / "unity-asset-store"
PRODUCT_URL = "https://assetstore.unity.com/packages/templates/systems/mech-combat-kit-164854"


@pytest.fixture
def planner():
    spec = importlib.util.spec_from_file_location(
        "plan_asset_acquisition", SKILL / "scripts" / "plan_asset_acquisition.py"
    )
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module.main


@pytest.mark.parametrize("suffix", ["", "/", "?srsltid=tracking#reviews", "/?utm_source=example"])
def test_product_link_plans_official_acquisition_without_claiming_download(
    planner, suffix, monkeypatch
):
    def reject_network(*_args, **_kwargs):
        pytest.fail("The acquisition planner must not access the network.")

    monkeypatch.setattr(socket, "socket", reject_network)
    result = planner(asset_url=PRODUCT_URL + suffix)
    assert result["success"] is True
    context = result["context"]
    assert context["asset_id"] == "164854"
    assert context["product_url"] == PRODUCT_URL
    assert context["acquisition_state"] == "requires_official_acquisition"
    assert context["download_supported"] is False
    assert context["import_supported"] is False
    assert len(context["acquisition_steps"]) == 4


@pytest.mark.parametrize(
    "asset_url",
    [
        None,
        "",
        PRODUCT_URL.replace("https:", "http:"),
        PRODUCT_URL.replace("assetstore.unity.com", "assetstore.unity.com.evil.test"),
        PRODUCT_URL.replace("assetstore.unity.com", "evil.test@assetstore.unity.com"),
        PRODUCT_URL.replace("assetstore.unity.com", "assetstore.unity.com:443"),
        PRODUCT_URL.replace("/templates/", "/../"),
        PRODUCT_URL.replace("/templates/", "/%2e%2e/"),
        PRODUCT_URL.replace("/templates/", "//"),
        PRODUCT_URL.replace("/templates/", "/templates\\/"),
        PRODUCT_URL.replace("164854", "0"),
        PRODUCT_URL.replace("164854", "-1"),
        PRODUCT_URL.replace("164854", "164854.zip"),
        PRODUCT_URL + "/download",
        PRODUCT_URL + "\n",
        "\t" + PRODUCT_URL,
        PRODUCT_URL + "\x00",
        PRODUCT_URL + "\x7f",
        PRODUCT_URL + "?tracking=\x80",
        PRODUCT_URL + "?" + "x" * 2048,
    ],
)
def test_untrusted_or_malformed_links_fail_closed(planner, asset_url):
    result = planner(asset_url=asset_url)
    assert result["success"] is False
    assert result["error"]
    assert "product_url" not in result["context"]


def test_planner_schema_covers_request_and_standard_result(planner):
    tool = yaml.safe_load((SKILL / "tools.yaml").read_text("utf-8"))["tools"][0]
    arguments = {"asset_url": PRODUCT_URL}
    Draft7Validator(tool["input_schema"]).validate(arguments)
    assert not Draft7Validator(tool["input_schema"]).is_valid({"asset_url": "x" * 2049})
    assert not Draft7Validator(tool["input_schema"]).is_valid({**arguments, "token": "secret"})
    Draft7Validator(tool["output_schema"]).validate(planner(**arguments))
