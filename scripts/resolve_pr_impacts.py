#!/usr/bin/env python3

"""Resolve impacted CI build groups and test stages for a PR.

This script determines which container builds and test stages need to run based
on which files changed in a pull request. It writes boolean outputs to
GITHUB_OUTPUT so GitHub Actions can gate downstream jobs.

Strategy (intentionally conservative — prefers false positives over false negatives):

1. SAFE SKIP — A tiny allowlist of metadata-only files (LICENSE, etc.) that can
   never affect builds. If ALL changed files are in this set, CI is skipped.

2. BROAD RUN — High-risk directories (build/, .github/, test/, etc.) that are
   too interconnected to classify cheaply. Any change here runs everything.

3. SELECTIVE — For changes purely under src/, the script parses the production
   build scripts and their Dockerfiles to discover which source directories each
   build group depends on. It then matches changed files against those roots.

4. FAIL OPEN — If changed files don't match any known dependency, a full run is
   triggered rather than risking a missed rebuild.

Outputs written to GITHUB_OUTPUT:
    run-build                — True if any build group needs to run.
    build-<group>            — One per build group (e.g., build-cgs-containers).
    run-test-<stage>         — One per test stage (e.g., run-test-cgs).
"""

from __future__ import annotations

import logging
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOGGER = logging.getLogger("resolve_pr_impacts")

# ---------------------------------------------------------------------------
# Configuration.
# ---------------------------------------------------------------------------

# Files guaranteed not to affect build or runtime behavior.
# Changes touching ONLY these files skip CI entirely.
SAFE_SKIP_FILES = {
    "CODE_OF_CONDUCT.md",
    "LICENSE",
    "SECURITY.md",
    "SUPPORT.md",
}

# Paths that always trigger a full CI run. These cover CI definitions, build
# infrastructure, shared configs, tests, and other areas where static
# misclassification would be expensive.
BROAD_RUN_PATHS = {
    ".github/",
    "build/",
    "docker/",
    "external/",
    "outdated/",
    "poc/",
    "samples/",
    "scripts/",
    "templates/",
    "test/",
    "workloads/",
    "Directory.Build.props",
    "Directory.Packages.props",
    "Menees.Analyzers.Settings.xml",
    "go.mod",
    "pyproject.toml",
    "stylecop.json",
}

# Maps each build group to the PowerShell build scripts that produce its images.
# Dependencies are extracted dynamically by parsing these scripts and their
# Dockerfiles (COPY sources, src/samples/templates path references).
GROUP_SCRIPTS = {
    "build-shared-containers": [
        "build/ccr/build-ccr-proxy.ps1",
        "build/ccr/build-skr.ps1",
        "build/ccr/build-ccr-governance.ps1",
        "build/ccr/build-ccr-governance-virtual.ps1",
        "build/ccr/build-local-skr.ps1",
        "build/ccr/build-local-idp.ps1",
        "build/ccr/build-otel-collector.ps1",
        "build/cvm/build-cvm-attestation-verifier.ps1",
        "build/cvm/build-cvm-attestation-agent.ps1",
        "build/k8s-node/build-api-server-proxy.ps1",
    ],
    "build-ccf-containers": [
        "build/ccf/build-ccf-provider-client.ps1",
        "build/ccf/build-ccf-recovery-agent.ps1",
        "build/ccf/build-ccf-recovery-service.ps1",
        "build/ccf/build-ccf-consortium-manager.ps1",
        "build/ccf/build-ccf-runjs-app-virtual.ps1",
        "build/ccf/build-ccf-runjs-app-snp.ps1",
        "build/ccf/build-ccf-runjs-app-sandbox.ps1",
    ],
    "build-cgs-containers": [
        "build/cgs/build-cgs-client.ps1",
        "build/cgs/build-cgs-ui.ps1",
        "build/cgs/build-cgs-ccf-artefacts.ps1",
    ],
    "build-ccr-containers": [
        "build/ccr/build-blobfuse-launcher.ps1",
        "build/ccr/build-s3fs-launcher.ps1",
        "build/ccr/build-ccr-init.ps1",
        "build/ccr/build-ccr-secrets.ps1",
        "build/ccr/build-ccr-proxy-ext-processor.ps1",
        "build/ccr/build-ccr-client-proxy.ps1",
        "build/ccr/build-code-launcher.ps1",
        "build/ccr/build-identity.ps1",
        "build/ccr/build-ccr-governance-opa-policy.ps1",
        "build/ccr/build-ccr-artefacts.ps1",
    ],
    "build-cleanroom-cluster-containers": [
        "build/cleanroom-cluster/build-cleanroom-cluster-provider-client.ps1",
    ],
    "build-analytics-workload-containers": [
        "build/workloads/analytics/build-cleanroom-spark-analytics-agent.ps1",
        "build/workloads/analytics/build-cleanroom-spark-frontend.ps1",
        "build/workloads/analytics/build-cleanroom-spark-analytics-app.ps1",
    ],
    "build-kserve-inferencing-workload-containers": [
        "build/workloads/inferencing/build-kserve-inferencing-agent.ps1",
        "build/workloads/inferencing/build-kserve-inferencing-frontend.ps1",
    ],
    "build-frontend-service-containers": [
        "build/workloads/frontend/build-frontend-service.ps1",
    ],
    "build-azcliext-cleanroom": [
        "build/build-azcliext-cleanroom.ps1",
    ],
}

# Maps each test stage to the build groups it depends on.
# A test stage runs if ANY of its required groups have direct changes.
# Derived from docker-compose files and workflow structure.
TEST_STAGE_GROUPS = {
    "test-cgs": [
        "build-shared-containers",
        "build-cgs-containers",
        # CGS tests deploy a CCF sandbox node via docker-compose.
        "build-ccf-containers",
    ],
    "test-ccf": [
        "build-shared-containers",
        "build-ccf-containers",
    ],
    "test-cleanroom-cluster": [
        "build-shared-containers",
        "build-ccr-containers",
        "build-cleanroom-cluster-containers",
        "build-analytics-workload-containers",
        "build-kserve-inferencing-workload-containers",
        "build-frontend-service-containers",
    ],
    "test-multi-party-collab": [
        "build-shared-containers",
        "build-ccr-containers",
        "build-analytics-workload-containers",
        "build-azcliext-cleanroom",
    ],
    "test-workloads": [
        "build-shared-containers",
        "build-cleanroom-cluster-containers",
        "build-analytics-workload-containers",
        "build-kserve-inferencing-workload-containers",
        "build-frontend-service-containers",
        "build-azcliext-cleanroom",
    ],
}

# If any of these build groups are impacted, build-shared-containers is also
# required because shared images (proxy, governance, attestation, etc.) are base
# dependencies for these groups.
SHARED_TRANSITIVE_GROUPS = [
    "build-ccf-containers",
    "build-ccr-containers",
    "build-analytics-workload-containers",
    "build-kserve-inferencing-workload-containers",
    "build-frontend-service-containers",
]


# ---------------------------------------------------------------------------
# Dependency extraction from build scripts and Dockerfiles.
# ---------------------------------------------------------------------------

# Matches Dockerfile references in build scripts (e.g., Dockerfile.proxy).
DOCKERFILE_RE = re.compile(r"Dockerfile\.[A-Za-z0-9_.-]+")

# Matches repo-tracked source paths in build scripts (e.g., src/governance/).
PATH_RE = re.compile(r"(?:src|samples|templates)/[A-Za-z0-9_./*-]+")

# Matches COPY instructions in Dockerfiles to extract source directories.
COPY_RE = re.compile(r"^\s*COPY(?:\s+--from=[^\s]+)?\s+(.+)$")


def normalize_dependency(path: str) -> str | None:
    """Normalize a referenced path into a directory-level dependency root.

    Build scripts and Dockerfiles reference individual files or globs. This maps
    them to their parent directory so matching is stable across file additions,
    deletions, and renames within a tracked source area.
    """
    value = path.strip().strip("`\"',")
    value = value.lstrip("./")
    if not value or value.startswith("$"):
        return None

    # Bare filenames: only track known root-level config files.
    if "/" not in value:
        return value if value in {"pyproject.toml", "uv.lock"} else None

    # Strip glob suffixes (e.g., src/foo/* → src/foo).
    if "*" in value:
        value = value.split("*")[0].rstrip("/")

    # Files (with extensions) map to their parent directory.
    suffix = Path(value).suffix.lower()
    if suffix or value.endswith((".sh", ".ps1")):
        parent = Path(value).parent.as_posix()
        return f"{parent}/" if parent != "." else None

    return f"{value.rstrip('/')}/"


def parse_dockerfile(path: Path) -> set[str]:
    """Extract repo-tracked COPY source roots from a Dockerfile."""
    deps: set[str] = set()
    if not path.exists():
        LOGGER.warning("Dockerfile not found: %s", path.relative_to(ROOT))
        return deps

    for line in path.read_text(encoding="utf-8").splitlines():
        match = COPY_RE.match(line)
        if match:
            # COPY tokens: all except the last (destination) are sources.
            for token in match.group(1).split()[:-1]:
                dep = normalize_dependency(token)
                if dep:
                    deps.add(dep)

    return deps


def parse_build_script(rel_path: str) -> set[str]:
    """Parse a PowerShell build script and discover dependency roots.

    Finds Dockerfile references (follows their COPY sources) and direct
    src/samples/templates path references in the script.
    """
    deps: set[str] = set()
    path = ROOT / rel_path
    if not path.exists():
        LOGGER.warning("Build script not found: %s", rel_path)
        return deps

    content = path.read_text(encoding="utf-8")

    # Follow Dockerfile references into build/docker/.
    for dockerfile_name in DOCKERFILE_RE.findall(content):
        dockerfile_deps = parse_dockerfile(ROOT / "build" / "docker" / dockerfile_name)
        deps.update(dockerfile_deps)

    # Capture direct source path references.
    for match in PATH_RE.findall(content):
        dep = normalize_dependency(match)
        if dep:
            deps.add(dep)

    return deps


def build_group_dependencies() -> dict[str, set[str]]:
    """Build dependency roots for each CI build group by parsing its scripts."""
    group_deps: dict[str, set[str]] = {}

    for group_name, scripts in GROUP_SCRIPTS.items():
        deps: set[str] = set()
        for script in scripts:
            deps.update(parse_build_script(script))
        group_deps[group_name] = deps
        LOGGER.info(
            "Dependencies for %s (%d roots): %s",
            group_name,
            len(deps),
            ", ".join(sorted(deps)) or "none",
        )

    return group_deps


# ---------------------------------------------------------------------------
# Change detection via git.
# ---------------------------------------------------------------------------


def run_git(args: list[str]) -> str:
    """Run a git command from the repository root and return stdout."""
    result = subprocess.run(
        ["git", *args],
        cwd=ROOT,
        check=True,
        text=True,
        capture_output=True,
    )
    return result.stdout


def get_changed_files() -> list[str]:
    """Return changed file paths for the PR, including both sides of renames."""
    base_sha = os.getenv("BASE_SHA", "")
    head_sha = os.getenv("HEAD_SHA", "")
    if not base_sha or not head_sha:
        raise RuntimeError("BASE_SHA and HEAD_SHA are required for pull_request runs.")

    output = run_git(
        ["diff", "--name-status", "--find-renames", f"{base_sha}...{head_sha}"]
    )

    paths: set[str] = set()
    for line in output.splitlines():
        parts = line.split("\t")
        if not parts:
            continue
        status = parts[0]
        # Renames and copies produce two paths (old and new).
        if status.startswith(("R", "C")) and len(parts) >= 3:
            paths.add(parts[1])
            paths.add(parts[2])
        elif len(parts) >= 2:
            paths.add(parts[1])

    return sorted(path for path in paths if path)


# ---------------------------------------------------------------------------
# Output resolution.
# ---------------------------------------------------------------------------


def resolve_from_groups(group_hits: dict[str, bool]) -> dict[str, bool]:
    """Resolve final CI outputs from per-group match results.

    Build outputs use transitive-expanded groups (shared containers are enabled
    if any dependent group needs them). Test stage outputs use direct group hits
    only — a test runs when its own code changed, not transitively.
    """
    # Apply transitive shared-container requirement for builds.
    build_groups = dict(group_hits)
    if any(build_groups.get(g, False) for g in SHARED_TRANSITIVE_GROUPS):
        LOGGER.info("Enabling build-shared-containers transitively.")
        build_groups["build-shared-containers"] = True

    # Build group outputs (with transitive expansion).
    outputs: dict[str, bool] = {}
    for group_name in GROUP_SCRIPTS:
        outputs[group_name] = build_groups.get(group_name, False)
    outputs["run-build"] = any(outputs.values())

    # Test stage outputs (using direct group hits, not transitive).
    for stage, required_groups in TEST_STAGE_GROUPS.items():
        enabled = any(group_hits.get(g, False) for g in required_groups)
        outputs[f"run-{stage}"] = enabled
        LOGGER.info(
            "Test stage %s: %s (requires: %s)",
            stage,
            "enabled" if enabled else "skipped",
            ", ".join(sorted(required_groups)),
        )

    return outputs


def write_outputs(outputs: dict[str, bool]) -> None:
    """Write outputs to GITHUB_OUTPUT as two comma-separated lists.

    Writes:
        build-groups — Comma-separated list of enabled build groups
                       (e.g., "build-shared-containers,build-cgs-containers").
                       Empty string if no builds needed.
        test-stages  — Comma-separated list of enabled test stages
                       (e.g., "test-cgs,test-ccf"). Empty string if none.
    """
    github_output = os.getenv("GITHUB_OUTPUT")
    if not github_output:
        raise RuntimeError("GITHUB_OUTPUT is not set.")

    # Split outputs into build groups and test stages.
    build_groups = sorted(k for k, v in outputs.items() if v and k in GROUP_SCRIPTS)
    test_stages = sorted(
        k.removeprefix("run-")
        for k, v in outputs.items()
        if v and k.startswith("run-test-")
    )

    with open(github_output, "a", encoding="utf-8") as f:
        f.write(f"build-groups={','.join(build_groups)}\n")
        f.write(f"test-stages={','.join(test_stages)}\n")

    LOGGER.info(
        "Build groups (%d): %s",
        len(build_groups),
        ", ".join(build_groups) or "none",
    )
    LOGGER.info(
        "Test stages (%d): %s",
        len(test_stages),
        ", ".join(test_stages) or "none",
    )


# ---------------------------------------------------------------------------
# Main entry point.
# ---------------------------------------------------------------------------


def main() -> int:
    """Resolve impacted build and test outputs for the current CI event."""
    logging.basicConfig(level=logging.INFO, format="[resolve_pr_impacts] %(message)s")

    event_name = os.getenv("CI_EVENT_NAME", "")
    LOGGER.info("Event: %s", event_name or "unknown")

    # Manual dispatch: run everything.
    if event_name == "workflow_dispatch":
        LOGGER.info("Manual dispatch — enabling all stages.")
        outputs = resolve_from_groups({g: True for g in GROUP_SCRIPTS})
        write_outputs(outputs)
        return 0

    changed_files = get_changed_files()
    LOGGER.info("Changed files (%d): %s", len(changed_files), ", ".join(changed_files))

    # If all changes are in guaranteed-safe files, skip CI entirely.
    if changed_files and all(f in SAFE_SKIP_FILES for f in changed_files):
        LOGGER.info("All changes are safe-skip metadata files — skipping CI.")
        outputs = resolve_from_groups({g: False for g in GROUP_SCRIPTS})
        write_outputs(outputs)
        return 0

    # If any change is in a broad-run path, run everything.
    broad = [
        f
        for f in changed_files
        if any(f == p or f.startswith(p) for p in BROAD_RUN_PATHS)
    ]
    if broad:
        LOGGER.info(
            "Broad-run trigger paths (%d): %s",
            len(broad),
            ", ".join(sorted(broad)),
        )
        LOGGER.info("High-risk paths detected — enabling full CI.")
        outputs = resolve_from_groups({g: True for g in GROUP_SCRIPTS})
        write_outputs(outputs)
        return 0

    # Selective resolution: parse build scripts to discover dependency roots,
    # then match changed files against those roots.
    group_deps = build_group_dependencies()
    group_hits = {
        group: any(
            (dep.endswith("/") and path.startswith(dep)) or path == dep
            for path in changed_files
            for dep in deps
        )
        for group, deps in group_deps.items()
    }
    LOGGER.info("Direct group matches: %s", group_hits)

    # If no group matched but there ARE changed files, fail open to full CI.
    if changed_files and not any(group_hits.values()):
        LOGGER.info("No dependency match found — failing open to full CI.")
        outputs = resolve_from_groups({g: True for g in GROUP_SCRIPTS})
        write_outputs(outputs)
        return 0

    outputs = resolve_from_groups(group_hits)
    write_outputs(outputs)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        logging.basicConfig(
            level=logging.INFO, format="[resolve_pr_impacts] %(message)s"
        )
        LOGGER.exception("Impact resolver failed: %s", exc)
        sys.exit(1)
