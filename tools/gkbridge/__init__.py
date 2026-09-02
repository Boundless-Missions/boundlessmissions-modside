"""gkbridge — drive the KSP mod's dev-only test bridge from Python.

Stdlib only. See client.py for the transport and scenarios.py for T0–T7.
"""
from .client import BridgeError, EventTap, Handshake, Instance, Refused, discover  # noqa: F401

__all__ = ["Instance", "Handshake", "EventTap", "BridgeError", "Refused", "discover"]
