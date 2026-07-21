"""Unity Editor adapter for DCC-MCP."""

from .__version__ import __version__
from .server import UnityMcpServer

__all__ = ["UnityMcpServer", "__version__"]
