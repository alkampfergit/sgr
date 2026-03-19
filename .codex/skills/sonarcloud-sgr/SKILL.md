---
name: sonarcloud-sgr
description: Use when working on SonarCloud issues for this repository, either to fix issues on the base branch backlog or to fix issues reported on the current pull request branch. Targets the public SonarCloud project alkampfergit_sgr.
---

# SonarCloud SGR

This skill is for SonarCloud work in this repository only.

- SonarCloud project key: `alkampfergit_sgr`
- Public project URL: `https://sonarcloud.io/project/overview?id=alkampfergit_sgr`
- Prefer the public Web API on the EU instance: `https://sonarcloud.io/api`

Use the helper script first:

```bash
python3 .codex/skills/sonarcloud-sgr/scripts/sonarcloud_issues.py --mode base
python3 .codex/skills/sonarcloud-sgr/scripts/sonarcloud_issues.py --mode pr
```

## Mode 1: Fix base branch issues

Use this when the goal is to reduce existing SonarCloud issues on the base branch.

Workflow:

1. Fetch the issue list with `--mode base`.
2. Work from the highest-signal items first.
3. Prefer issues with a precise file/line, then reliability/security issues, then maintainability issues.
4. Make small focused fixes that preserve behavior.
5. Run the narrowest useful verification first, then broader build/test if the change is shared.
6. Summarize which Sonar issues were addressed and what still remains.

Notes:

- `--mode base` tries to use the base branch of the current PR when `gh pr view` works.
- If PR metadata is unavailable, it falls back to `main`.

## Mode 2: Fix current pull request issues

Use this when the goal is to fix only the issues introduced by the current branch's PR.

Workflow:

1. Fetch the PR issue list with `--mode pr`.
2. Restrict fixes to issues reported on the PR unless the user asks for broader cleanup.
3. Prefer minimal diffs that directly satisfy the rule without refactoring unrelated code.
4. Re-run verification for the touched code.
5. Report which PR issues were fixed and which ones still need follow-up.

Notes:

- `--mode pr` uses `gh pr view --json number,baseRefName,headRefName,url` when available.
- If `gh` is unavailable or unauthenticated, rerun with `--pr <number>`.

## Working rules

- Do not mark issues as accepted or false positive unless the user explicitly asks.
- Fix the code rather than silencing the rule unless the rule is clearly wrong for this codebase.
- Keep changes local and readable.
- Mention the Sonar rule key in the summary when it is available.

## Helper script

The helper script prints a concise list with:

- issue key
- rule key
- severity and type
- file and line
- message

Optional flags:

```bash
python3 .codex/skills/sonarcloud-sgr/scripts/sonarcloud_issues.py --mode base --branch main
python3 .codex/skills/sonarcloud-sgr/scripts/sonarcloud_issues.py --mode pr --pr 123
python3 .codex/skills/sonarcloud-sgr/scripts/sonarcloud_issues.py --mode pr --raw
```
