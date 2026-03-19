#!/usr/bin/env python3
"""Fetch SonarCloud issues for this repository's public project."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import urllib.parse
import urllib.request
from typing import Any


DEFAULT_PROJECT = "alkampfergit_sgr"
DEFAULT_BASE_URL = "https://sonarcloud.io/api"


def run_command(args: list[str]) -> str | None:
    try:
        completed = subprocess.run(
            args,
            check=True,
            capture_output=True,
            text=True,
        )
    except (FileNotFoundError, subprocess.CalledProcessError):
        return None

    return completed.stdout.strip()


def detect_pr_metadata() -> dict[str, Any] | None:
    output = run_command(
        ["gh", "pr", "view", "--json", "number,baseRefName,headRefName,url"]
    )
    if not output:
        return None

    try:
        return json.loads(output)
    except json.JSONDecodeError:
        return None


def build_url(
    *,
    base_url: str,
    project: str,
    branch: str | None,
    pull_request: str | None,
) -> str:
    params = {
        "componentKeys": project,
        "resolved": "false",
        "ps": "100",
        "s": "FILE_LINE",
        "asc": "true",
    }
    if branch:
        params["branch"] = branch
    if pull_request:
        params["pullRequest"] = pull_request

    query = urllib.parse.urlencode(params)
    return f"{base_url.rstrip('/')}/issues/search?{query}"


def fetch_json(url: str) -> dict[str, Any]:
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/json",
            "User-Agent": "codex-sonarcloud-sgr-skill",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def format_component(component: str) -> str:
    parts = component.split(":")
    return parts[-1] if parts else component


def print_summary(payload: dict[str, Any], context_label: str) -> None:
    paging = payload.get("paging", {})
    total = payload.get("total", 0)
    issues = payload.get("issues", [])

    print(f"Context: {context_label}")
    print(f"Total unresolved issues returned: {len(issues)} / {total}")

    if not issues:
        return

    for issue in issues:
        line = issue.get("line")
        line_text = f":{line}" if line else ""
        component = format_component(issue.get("component", "<unknown>"))
        rule = issue.get("rule", "<no-rule>")
        severity = issue.get("severity", "<no-severity>")
        issue_type = issue.get("type", "<no-type>")
        message = issue.get("message", "").strip().replace("\n", " ")
        key = issue.get("key", "<no-key>")
        print(
            f"- {key} | {rule} | {severity}/{issue_type} | {component}{line_text} | {message}"
        )

    if total > paging.get("pageSize", 0):
        print("Note: results were truncated by the API page size.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["base", "pr"], required=True)
    parser.add_argument("--project", default=DEFAULT_PROJECT)
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--branch")
    parser.add_argument("--pr")
    parser.add_argument("--raw", action="store_true")
    return parser.parse_args()


def resolve_context(args: argparse.Namespace, pr_metadata: dict[str, Any] | None) -> tuple[str | None, str | None, str] | None:
    branch = args.branch
    pull_request = args.pr

    if args.mode == "base":
        if not branch and pr_metadata:
            branch = pr_metadata.get("baseRefName")
        if not branch:
            branch = "main"
        return branch, None, f"base branch '{branch}'"

    if not pull_request and pr_metadata:
        pr_value = pr_metadata.get("number")
        pull_request = str(pr_value) if pr_value is not None else None
    if not pull_request:
        return None

    return None, pull_request, f"pull request '{pull_request}'"


def main() -> int:
    args = parse_args()
    pr_metadata = detect_pr_metadata()
    context = resolve_context(args, pr_metadata)
    if context is None:
        print(
            "Unable to determine the current pull request. "
            "Provide it explicitly with --pr <number>.",
            file=sys.stderr,
        )
        return 2

    branch, pull_request, context_label = context

    url = build_url(
        base_url=args.base_url,
        project=args.project,
        branch=branch,
        pull_request=pull_request,
    )

    try:
        payload = fetch_json(url)
    except Exception as ex:  # pragma: no cover - best effort utility
        print(f"Failed to fetch SonarCloud issues from {url}: {ex}", file=sys.stderr)
        return 1

    if args.raw:
        print(json.dumps(payload, indent=2, sort_keys=True))
        return 0

    print_summary(payload, context_label)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
