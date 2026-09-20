"""Structured XML-fragment audit logging for AI chat interactions."""

# Copyright (c) 2026 S.Yukisita
# SPDX-License-Identifier: MIT

from __future__ import annotations

import json
import logging
import uuid
from datetime import datetime, timezone
from logging.handlers import TimedRotatingFileHandler
from pathlib import Path
from typing import Any
from xml.sax.saxutils import escape as xml_escape

from .actions import ACTION_PATTERN


class _XmlFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        xml_content = getattr(record, "xml_content", None)
        if isinstance(xml_content, str):
            return xml_content

        timestamp = datetime.fromtimestamp(
            record.created,
            tz=timezone.utc,
        ).isoformat()
        return (
            f'<log timestamp="{timestamp}" level="{record.levelname}">'
            f"{xml_escape(record.getMessage())}</log>"
        )


class ChatLogger:
    """Write one XML fragment per completed chat interaction."""

    def __init__(
        self,
        *,
        log_dir: Path,
        retention_days: int,
        max_field_length: int,
    ) -> None:
        self._max_field_length = max_field_length
        self._logger = _create_logger(log_dir, retention_days)

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
        actions = _extract_actions(final_response)
        parts = [
            (
                f'<interaction id="{uuid.uuid4().hex[:12]}" '
                f'timestamp="{_utc_now()}">'
            ),
            "  <request>",
            (
                "    <user_message>"
                f"{self._escape_and_truncate(user_message)}"
                "</user_message>"
            ),
            f"    <page_title>{xml_escape(page_title)}</page_title>",
            f"    <project_name>{xml_escape(project_name)}</project_name>",
        ]

        if page_context:
            parts.append(
                "    <page_context>"
                f"{self._escape_and_truncate(page_context)}"
                "</page_context>"
            )

        parts.extend(
            [
                "  </request>",
                "  <llm_output>",
                (
                    "    <raw>"
                    f"{self._escape_and_truncate(llm_raw_output)}"
                    "</raw>"
                ),
                "  </llm_output>",
                "  <response>",
                (
                    "    <final>"
                    f"{self._escape_and_truncate(final_response)}"
                    "</final>"
                ),
            ]
        )

        if actions:
            parts.append("    <actions>")
            parts.extend(
                f"      <action>{xml_escape(action)}</action>"
                for action in actions
            )
            parts.append("    </actions>")

        parts.append("  </response>")

        if warnings:
            parts.append("  <warnings>")
            parts.extend(
                f"    <warning>{xml_escape(warning)}</warning>"
                for warning in warnings
            )
            parts.append("  </warnings>")

        if duration_ms is not None:
            parts.append(f"  <duration_ms>{duration_ms}</duration_ms>")

        parts.append("</interaction>")
        self._write("\n".join(parts))

    def _escape_and_truncate(self, text: str) -> str:
        if len(text) > self._max_field_length:
            text = text[: self._max_field_length] + "…(truncated)"
        return xml_escape(text)

    def _write(self, xml_content: str) -> None:
        self._logger.info("", extra={"xml_content": xml_content})


def _create_logger(log_dir: Path, retention_days: int) -> logging.Logger:
    log_dir.mkdir(parents=True, exist_ok=True)
    log_file = (log_dir / "chat_interactions.xml").resolve()
    logger = logging.getLogger(f"crec_chat_audit.{log_file}")
    logger.setLevel(logging.INFO)
    logger.propagate = False

    if logger.handlers:
        return logger

    handler = TimedRotatingFileHandler(
        filename=str(log_file),
        when="midnight",
        interval=1,
        backupCount=retention_days,
        encoding="utf-8",
        utc=True,
    )
    handler.suffix = "%Y-%m-%d"
    handler.setFormatter(_XmlFormatter())
    logger.addHandler(handler)
    return logger


def _extract_actions(text: str) -> list[str]:
    actions: list[str] = []
    for match in ACTION_PATTERN.finditer(text):
        raw_action = match.group(1).strip()
        try:
            parsed: Any = json.loads(raw_action)
            actions.append(
                json.dumps(parsed, ensure_ascii=False, separators=(",", ":"))
            )
        except json.JSONDecodeError:
            actions.append(raw_action)
    return actions


def _utc_now() -> str:
    return datetime.now(tz=timezone.utc).isoformat()
