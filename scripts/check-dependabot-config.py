#!/usr/bin/env python3
"""Fails when Dependabot is not actually applying .github/dependabot.yml.

GitHub validates this file on any pull request that touches it, and rejects an
invalid one with a named error -- that gap is covered. What is not covered is
the state *after* an invalid config reaches the default branch: Dependabot
falls back to the last version that parsed and keeps running from it silently.
Nothing re-checks the file, because the validator only fires on pull requests
that modify it. A config broken by a direct push, or broken before the
validator existed, stays broken and says nothing.

That is not hypothetical. WidgetWorks ran a rejected config from 2026-08-07 to
2026-08-31. Every edit in between -- including one written specifically to stop
codeql-action/init and /analyze being proposed as separate, unmergeable pull
requests -- was never applied. The split recurred four weeks running and was
fixed by hand each time, because the file on disk looked correct.

The tell was in Dependabot's own output: it kept opening pull requests for a
group named `dotnet-minor-patch`, which the config had stopped declaring three
weeks earlier. This script checks exactly that, and deliberately tests the
outcome rather than the mechanism -- it does not care *why* the config is not
being applied, only that Dependabot's behaviour disagrees with the file.

A second failure, found 2026-10-29, is checked the same way and for the same
reason. The npm block pointed at /src/WidgetWorks.Web, which had not existed
since the SPA moved to /web. That block's weekly run failed every Monday and
proposed nothing, so its groups were inert -- yet security updates kept
arriving, because Dependabot finds those through the dependency graph rather
than the configured directory. The block therefore looked half-alive, and the
only visible symptom was a red run nobody was reading.

That check reads the checkout rather than Dependabot's output, so unlike the
group comparison it is conclusive immediately and never reports inconclusive.

Exit codes: 0 pass or inconclusive, 1 drift or a missing directory detected.
"""

import glob
import json
import os
import re
import sys
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

try:
    import yaml
except ImportError:
    sys.exit("PyYAML is required: python -m pip install pyyaml")

CONFIG_PATH = ".github/dependabot.yml"

# Dependabot regenerates pull requests as a config change lands, so a rename can
# briefly produce a pull request under the old group name that is already
# obsolete when it appears. Ignore anything created within this window of the
# config's own last commit rather than reporting a race as drift.
GRACE = timedelta(hours=6)

# How far back to look. Dependabot here runs weekly, so this spans several runs
# without reaching back to configs that are no longer relevant.
LOOKBACK_DAYS = 45

# Dependabot names the group in two different title shapes, and matching only
# the first one both loses real drift and reports grouped pull requests as
# ungrouped:
#   multi-update: "bump the <name> group with 3 updates"
#                 "bump the <name> group in /web with 9 updates"
#   single:       "bump azure/login from 3.0.1 to 3.0.2 in the <name> group"
# `re.search` takes the leftmost match, so the first shape still wins when a
# title happens to contain both "the ... group" and " in /path".
GROUP_IN_TITLE = re.compile(
    r"\b(?:bump(?:ing)?\s+the|in\s+the)\s+(.+?)\s+group\b", re.IGNORECASE
)

# Dependabot encodes the manager in its branch name -- dependabot/<manager>/...
# -- which is a more reliable way to attribute a pull request to an ecosystem
# than parsing the title. Most manager names match package-ecosystem verbatim;
# these are the ones that do not.
MANAGER_TO_ECOSYSTEM = {
    "github_actions": "github-actions",
    "npm_and_yarn": "npm",
    "go_modules": "gomod",
    "submodules": "gitsubmodule",
}


def api(path):
    """GET a GitHub API path, returning parsed JSON."""
    request = urllib.request.Request(
        f"https://api.github.com{path}",
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": "check-dependabot-config",
            **(
                {"Authorization": f"Bearer {os.environ['GITHUB_TOKEN']}"}
                if os.environ.get("GITHUB_TOKEN")
                else {}
            ),
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        sys.exit(f"GitHub API {error.code} for {path}: {error.read()[:200]!r}")


def configured_directories(config):
    """Every directory an update block points at, as (ecosystem, path) pairs.

    Dependabot accepts a single `directory` or a `directories` list, and the
    list form may contain globs. Both are normalised here to repository-relative
    paths so they can be checked against the checkout.
    """
    pairs = []
    for update in config.get("updates") or []:
        ecosystem = update.get("package-ecosystem")
        entries = update.get("directories") or []
        if update.get("directory"):
            entries = [*entries, update["directory"]]
        for entry in entries:
            pairs.append((ecosystem, str(entry)))
    return pairs


def missing_directories(config):
    """Configured directories that do not exist in the checkout.

    A directory that is not there is the quietest failure Dependabot has. The
    weekly run for that block errors, no version update is ever proposed, and
    the only thing that still arrives is security updates -- which come from
    the dependency graph, not from this path -- so the block looks half-alive
    instead of dead. WidgetWorks pointed npm at /src/WidgetWorks.Web long after
    the SPA moved to /web and nothing said so.

    Globs in `directories` are honoured: a pattern matching nothing is as dead
    as a literal path that does not exist.
    """
    missing = []
    for ecosystem, path in configured_directories(config):
        relative = path.lstrip("/")
        # A bare "/" is the repository root, which always exists.
        if not relative:
            continue
        if any(character in relative for character in "*?["):
            if not glob.glob(relative):
                missing.append((ecosystem, path, "matches nothing"))
        elif not os.path.isdir(relative):
            missing.append((ecosystem, path, "does not exist"))
    return missing


def declared_groups(config):
    """Every group name the config declares, across all ecosystems."""
    names = set()
    for update in config.get("updates") or []:
        names.update((update.get("groups") or {}).keys())
    return names


def catch_all_ecosystems(config):
    """Ecosystems where every version update should arrive inside a group.

    Only counts a group that catches everything unconditionally: `patterns`
    including `*`, with no `update-types` narrowing it. A group restricted to
    minor and patch legitimately leaves majors ungrouped, so it says nothing
    about a lone pull request.
    """
    ecosystems = set()
    for update in config.get("updates") or []:
        for group in (update.get("groups") or {}).values():
            group = group or {}
            if "update-types" in group:
                continue
            if group.get("applies-to", "version-updates") != "version-updates":
                continue
            if "*" in (group.get("patterns") or []):
                ecosystems.add(update.get("package-ecosystem"))
    return ecosystems


def ecosystem_of(pull):
    """The package-ecosystem a Dependabot pull request belongs to, or None."""
    parts = ((pull.get("head") or {}).get("ref") or "").split("/")
    if len(parts) < 2 or parts[0] != "dependabot":
        return None
    return MANAGER_TO_ECOSYSTEM.get(parts[1], parts[1])


def main():
    repository = os.environ.get("GITHUB_REPOSITORY")
    if not repository:
        sys.exit("GITHUB_REPOSITORY is not set.")

    if not os.path.exists(CONFIG_PATH):
        print(f"No {CONFIG_PATH}; nothing to check.")
        return 0

    with open(CONFIG_PATH, encoding="utf-8") as handle:
        config = yaml.safe_load(handle)

    # Checked first, and separately, because it is the one part of this script
    # that is conclusive on its own: it reads the checkout rather than
    # Dependabot's output, so it needs no weekly run to have happened and can
    # never come back "inconclusive".
    missing = missing_directories(config)
    if missing:
        print("MISSING DIRECTORIES: Dependabot is configured to scan paths "
              "that are not in this repository.")
        print()
        for ecosystem, path, reason in missing:
            print(f"  {ecosystem}: '{path}' {reason}")
        print()
        print("That block's weekly run fails and proposes nothing, so no "
              "version update -- and no group declared under it -- has any")
        print("effect. Security updates still arrive, because those come from "
              "the dependency graph rather than this path, which is what makes")
        print("the block look half-alive instead of dead.")
        print()
        print("Point each entry at the directory holding the manifest, or "
              "remove the block if the code is gone.")

    # A missing directory is a failure on its own, whatever the group check
    # goes on to conclude -- including when it concludes nothing.
    failed = 1 if missing else 0
    if missing:
        print()

    groups = declared_groups(config)
    if not groups:
        # Without groups there is no name to compare against, so this check has
        # nothing to say. It is not a failure -- grouping is optional.
        print("Config declares no groups; drift cannot be detected this way.")
        return failed

    commits = api(f"/repos/{repository}/commits?path={CONFIG_PATH}&per_page=1")
    if not commits:
        print(f"No commit history for {CONFIG_PATH}; skipping.")
        return failed
    changed_at = datetime.fromisoformat(
        commits[0]["commit"]["committer"]["date"].replace("Z", "+00:00")
    )

    # Only pull requests Dependabot opened from the current config can say
    # anything about whether the current config is live.
    cutoff = max(
        changed_at + GRACE,
        datetime.now(timezone.utc) - timedelta(days=LOOKBACK_DAYS),
    )

    pulls = api(
        f"/repos/{repository}/pulls"
        "?state=all&sort=created&direction=desc&per_page=100"
    )

    grouped_ecosystems = catch_all_ecosystems(config)
    considered, drifted, ungrouped = [], [], []
    for pull in pulls:
        if (pull.get("user") or {}).get("login") != "dependabot[bot]":
            continue
        created = datetime.fromisoformat(pull["created_at"].replace("Z", "+00:00"))
        if created <= cutoff:
            continue
        considered.append(pull)

        match = GROUP_IN_TITLE.search(pull["title"] or "")
        if match:
            if match.group(1) not in groups:
                drifted.append((pull, match.group(1)))
        elif ecosystem_of(pull) in grouped_ecosystems:
            # A lone pull request in an ecosystem whose every version update
            # should have been grouped. Corroborating rather than conclusive:
            # security updates bypass version-update groups by design, and
            # they are not distinguishable from this endpoint.
            ungrouped.append(pull)

    print(f"Config last changed: {changed_at:%Y-%m-%d %H:%M UTC}")
    print(f"Declared groups:     {', '.join(sorted(groups))}")
    print(f"Dependabot PRs considered (created after {cutoff:%Y-%m-%d %H:%M UTC}): "
          f"{len(considered)}")

    if not considered:
        # Silence here means no evidence either way, not a clean bill of health.
        # Saying so is the point: a green check that proved nothing is exactly
        # the failure this script exists to stop repeating.
        print()
        print("INCONCLUSIVE: Dependabot has opened nothing since the config "
              "changed, so there is no output to compare against. This check "
              "proves nothing yet; it will have evidence after the next run.")
        return failed

    if ungrouped:
        print()
        print("Ungrouped pull requests in ecosystems that group everything:")
        for pull in ungrouped:
            print(f"  #{pull['number']}  {pull['title']}")
        print("  (Security updates are exempt from version-update groups, so")
        print("   this is a hint rather than a verdict -- but several at once,")
        print("   or a repeating pair, is worth opening the config over.)")

    if not drifted:
        print()
        print("OK: every group Dependabot named is declared in the config.")
        return failed

    print()
    print("DRIFT: Dependabot is opening pull requests for groups this config "
          "does not declare.")
    print()
    for pull, group in drifted:
        print(f"  #{pull['number']}  group '{group}' is not in {CONFIG_PATH}")
        print(f"      {pull['title']}")
        print(f"      {pull['html_url']}")
    print()
    print("Dependabot is almost certainly running an older version of the file.")
    print("It falls back to the last configuration that parsed and reports")
    print("nothing, so edits since then have had no effect -- including any")
    print("fix someone believes is already in place.")
    print()
    print("To confirm and fix:")
    print(f"  1. Open a pull request touching {CONFIG_PATH}. GitHub attaches a")
    print(f"     '{CONFIG_PATH}' check that names the offending property.")
    print("  2. Fix that property. One invalid key rejects the whole file, not")
    print("     just the block containing it.")
    print("  3. Confirm the next Dependabot run uses the declared group names.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
