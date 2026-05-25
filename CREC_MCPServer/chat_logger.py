"""
Structured XML logger for CREC Web AI chat interactions.

Logs each chat interaction as a structured XML element containing:
- timestamp, request ID
- user input (message, page context, page title)
- LLM raw output (before sanitization)
- final response (after sanitization/blocking)
- actions extracted from the response
- any warnings (hallucination detected, blocked actions, etc.)

Log files are rotated daily and stored in the `logs/` directory relative to
the server's working directory.  The log format is designed to be easily
parsed and searched by standard XML tools.

Usage:
    from chat_logger import chat_log

    chat_log.interaction(
        user_message="検索して",
        page_title="CREC Web",
        page_context="...",
        llm_raw_output="...",
        final_response="...",
        warnings=["hallucination_detected"],
        actions=[{"type": "search", "text": "カメラ"}],
    )
"""

import json
import logging
import os
import re
import uuid
from datetime import datetime, timezone
from logging.handlers import TimedRotatingFileHandler
from pathlib import Path
from typing import Any
from xml.sax.saxutils import escape as xml_escape

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

# Directory for log files (relative to CWD or absolute)
LOG_DIR: str = os.getenv("CHAT_LOG_DIR", "logs")

# How many days of log files to keep (0 = keep all)
LOG_RETENTION_DAYS: int = int(os.getenv("CHAT_LOG_RETENTION_DAYS", "90"))

# Maximum length of content fields in log (to prevent huge log entries)
LOG_MAX_FIELD_LENGTH: int = int(os.getenv("CHAT_LOG_MAX_FIELD_LENGTH", "10000"))

# Regex to find <action>…</action> blocks
_ACTION_RE: re.Pattern = re.compile(r"<action>([\s\S]*?)</action>")


# ---------------------------------------------------------------------------
# XML Formatter
# ---------------------------------------------------------------------------

class _XmlChatFormatter(logging.Formatter):
    """Format log records as XML elements."""

    def format(self, record: logging.LogRecord) -> str:
        # For non-chat records, fall back to simple text format
        if not hasattr(record, "xml_content"):
            ts = datetime.fromtimestamp(record.created, tz=timezone.utc).isoformat()
            return (
                f'<log timestamp="{ts}" level="{record.levelname}">'
                f"{xml_escape(record.getMessage())}</log>"
            )
        return record.xml_content  # type: ignore[attr-defined]


# ---------------------------------------------------------------------------
# Logger setup
# ---------------------------------------------------------------------------

def _setup_logger() -> logging.Logger:
    """Create and configure the chat interaction logger."""
    log_dir = Path(LOG_DIR)
    log_dir.mkdir(parents=True, exist_ok=True)

    logger = logging.getLogger("crec_chat_audit")
    logger.setLevel(logging.INFO)

    # Avoid duplicate handlers on reimport
    if logger.handlers:
        return logger

    log_file = log_dir / "chat_interactions.xml"

    handler = TimedRotatingFileHandler(
        filename=str(log_file),
        when="midnight",
        interval=1,
        backupCount=LOG_RETENTION_DAYS,
        encoding="utf-8",
        utc=True,
    )
    # Rotated files get a date suffix: chat_interactions.xml.2026-05-25
    handler.suffix = "%Y-%m-%d"

    handler.setFormatter(_XmlChatFormatter())
    logger.addHandler(handler)

    # Don't propagate to root logger
    logger.propagate = False

    return logger


_logger = _setup_logger()


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

class ChatLogger:
    """Structured logger for AI chat interactions."""

    @staticmethod
    def _truncate(text: str) -> str:
        """Truncate text to LOG_MAX_FIELD_LENGTH."""
        if len(text) > LOG_MAX_FIELD_LENGTH:
            return text[:LOG_MAX_FIELD_LENGTH] + "…(truncated)"
        return text

    @staticmethod
    def _extract_actions(text: str) -> list[dict[str, Any]]:
        """Extract action payloads from response text."""
        actions = []
        for match in _ACTION_RE.finditer(text):
            try:
                actions.append(json.loads(match.group(1).strip()))
            except json.JSONDecodeError:
                actions.append({"raw": match.group(1).strip()})
        return actions

    @staticmethod
    def _format_actions_xml(actions: list[dict[str, Any]]) -> str:
        """Format actions list as XML elements."""
        if not actions:
            return ""
        lines = []
        for action in actions:
            action_json = json.dumps(action, ensure_ascii=False)
            lines.append(f"    <action>{xml_escape(action_json)}</action>")
        return "\n".join(lines)

    def interaction(
        self,
        *,
        user_message: str,
        page_title: str = "",
        page_context: str = "",
        project_name: str = "",
        llm_raw_output: str = "",
        final_response: str = "",
        warnings: list[str] | None = None,
        duration_ms: int | None = None,
    ) -> None:
        """Log a complete chat interaction as a structured XML element.

        Args:
            user_message:   The user's input message.
            page_title:     Browser page title at time of request.
            page_context:   Page context sent to the LLM (may be truncated).
            project_name:   CREC Web project name.
            llm_raw_output: Raw LLM response before sanitization.
            final_response: Response after sanitization/blocking (sent to user).
            warnings:       List of warning codes (e.g. "hallucination_detected",
                           "blocked_deletion", "empty_response").
            duration_ms:    LLM request duration in milliseconds.
        """
        request_id = uuid.uuid4().hex[:12]
        timestamp = datetime.now(tz=timezone.utc).isoformat()

        actions = self._extract_actions(final_response)
        warnings = warnings or []

        # Build XML
        parts = [
            f'<interaction id="{request_id}" timestamp="{timestamp}">',
            f"  <request>",
            f"    <user_message>{xml_escape(self._truncate(user_message))}</user_message>",
            f"    <page_title>{xml_escape(page_title)}</page_title>",
            f"    <project_name>{xml_escape(project_name)}</project_name>",
        ]

        if page_context:
            parts.append(
                f"    <page_context>{xml_escape(self._truncate(page_context))}</page_context>"
            )

        parts.append(f"  </request>")

        parts.append(f"  <llm_output>")
        parts.append(
            f"    <raw>{xml_escape(self._truncate(llm_raw_output))}</raw>"
        )
        parts.append(f"  </llm_output>")

        parts.append(f"  <response>")
        parts.append(
            f"    <final>{xml_escape(self._truncate(final_response))}</final>"
        )

        if actions:
            parts.append(f"  <actions>")
            parts.append(self._format_actions_xml(actions))
            parts.append(f"  </actions>")

        parts.append(f"  </response>")

        if warnings:
            parts.append(f"  <warnings>")
            for w in warnings:
                parts.append(f"    <warning>{xml_escape(w)}</warning>")
            parts.append(f"  </warnings>")

        if duration_ms is not None:
            parts.append(f"  <duration_ms>{duration_ms}</duration_ms>")

        parts.append(f"</interaction>")

        xml_content = "\n".join(parts)

        record = logging.LogRecord(
            name="crec_chat_audit",
            level=logging.INFO,
            pathname="",
            lineno=0,
            msg="",
            args=None,
            exc_info=None,
        )
        record.xml_content = xml_content  # type: ignore[attr-defined]
        _logger.handle(record)

    def warning(self, message: str) -> None:
        """Log a non-interaction warning or error."""
        timestamp = datetime.now(tz=timezone.utc).isoformat()
        xml_content = (
            f'<log timestamp="{timestamp}" level="WARNING">'
            f"{xml_escape(message)}</log>"
        )
        record = logging.LogRecord(
            name="crec_chat_audit",
            level=logging.WARNING,
            pathname="",
            lineno=0,
            msg="",
            args=None,
            exc_info=None,
        )
        record.xml_content = xml_content  # type: ignore[attr-defined]
        _logger.handle(record)


# Singleton instance
chat_log = ChatLogger()
