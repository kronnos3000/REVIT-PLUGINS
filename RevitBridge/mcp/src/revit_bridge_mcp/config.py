"""Runtime configuration and logging for the revit-bridge MCP server.

Settings come from environment variables (optionally via a local ``.env`` file).
Logging is sent to **stderr** and/or a file — never stdout, because for an
stdio MCP server stdout carries the JSON-RPC protocol and any stray print there
will corrupt the connection to Claude Desktop.
"""

from __future__ import annotations

import logging
import os
import sys
from dataclasses import dataclass
from logging.handlers import RotatingFileHandler

try:  # python-dotenv is optional at runtime
    from dotenv import load_dotenv

    load_dotenv()
except Exception:  # pragma: no cover - dotenv not installed
    pass


def _as_int(value: str | None) -> int | None:
    try:
        return int(value) if value and value.strip() else None
    except ValueError:
        return None


def _as_float(value: str | None, default: float) -> float:
    try:
        return float(value) if value and value.strip() else default
    except ValueError:
        return default


@dataclass(frozen=True)
class Settings:
    """Server settings resolved from the environment."""

    pid: int | None = None
    timeout_s: float = 60.0
    log_level: str = "INFO"
    log_file: str | None = None

    @classmethod
    def from_env(cls) -> "Settings":
        return cls(
            pid=_as_int(os.getenv("REVIT_BRIDGE_PID")),
            timeout_s=_as_float(os.getenv("REVIT_BRIDGE_TIMEOUT_S"), 60.0),
            log_level=os.getenv("REVIT_BRIDGE_LOG_LEVEL", "INFO").upper(),
            log_file=os.getenv("REVIT_BRIDGE_LOG_FILE") or None,
        )


settings = Settings.from_env()


def setup_logging(cfg: Settings = settings) -> logging.Logger:
    """Configure and return the package logger (stderr + optional rotating file)."""
    logger = logging.getLogger("revit_bridge_mcp")
    logger.setLevel(getattr(logging, cfg.log_level, logging.INFO))
    logger.handlers.clear()
    logger.propagate = False

    fmt = logging.Formatter("%(asctime)s %(levelname)s [%(name)s] %(message)s")

    # CRITICAL: stderr only. stdout is reserved for the MCP JSON-RPC stream.
    stderr_handler = logging.StreamHandler(sys.stderr)
    stderr_handler.setFormatter(fmt)
    logger.addHandler(stderr_handler)

    if cfg.log_file:
        file_handler = RotatingFileHandler(
            cfg.log_file, maxBytes=2_000_000, backupCount=3, encoding="utf-8"
        )
        file_handler.setFormatter(fmt)
        logger.addHandler(file_handler)

    return logger


logger = setup_logging()
