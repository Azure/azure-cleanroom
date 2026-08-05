#!/usr/bin/env python3

"""Resolve impacted CI build groups and test stages for a PR.

This script determines which container builds and test stages need to run based
on which files changed in a pull request. It writes comma-separated list outputs
to GITHUB_OUTPUT so GitHub Actions can gate downstream jobs.

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
    build-groups — Comma-separated list of enabled build groups
                   (e.g., "build-ccr-governance-containers,build-cgs-containers").
                   Empty string if no builds needed.
    test-stages  — Comma-separated list of enabled test stages
                   (e.g., "test-cgs,test-ccf"). Empty string if none.
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
    "build-ccr-governance-containers": [
        "build/ccr/build-ccr-governance.ps1",
        "build/ccr/build-ccr-governance-virtual.ps1",
    ],
    "build-ccr-proxy-containers": [
        "build/ccr/build-ccr-proxy.ps1",
    ],
    "build-ccr-skr-containers": [
        "build/ccr/build-skr.ps1",
        "build/ccr/build-local-skr.ps1",
    ],
    "build-ccr-tools-containers": [
        "build/ccr/build-local-idp.ps1",
    ],
    "build-cvm-containers": [
        "build/cvm/build-cvm-attestation-verifier.ps1",
        "build/cvm/build-cvm-attestation-agent.ps1",
    ],
    "build-k8s-node-containers": [
        "build/k8s-node/build-api-server-proxy.ps1",
        "build/k8s-node/build-kubelet-proxy.ps1",
        "build/k8s-node/build-cleanroom-boot.ps1",
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
        "build/ccr/build-otel-collector.ps1",
        "build/ccr/build-cleanroom-csi-driver.ps1",
    ],
    "build-cleanroom-cluster-containers": [
        "build/cleanroom-cluster/build-cleanroom-cluster-provider-client.ps1",
    ],
    "build-cleanroom-operator-containers": [
        "build/cleanroom-operator/build-cleanroom-operator.ps1",
        "build/cleanroom-operator/build-kubectl-cleanroom.ps1",
        "build/cleanroom-operator/build-karpenter-provider-accr.ps1",
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

# Runtime dependencies: maps each test stage to ALL build groups it needs.
# When a test stage is triggered, every group listed here is built so the test
# has all the container images it requires.
# Derived from docker-compose files and workflow structure.
TEST_STAGE_GROUPS = {
    "test-cgs": [
        "build-ccr-governance-containers",
        "build-cvm-containers",
        "build-ccr-tools-containers",
        "build-cgs-containers",
        # CGS tests deploy a CCF sandbox node via docker-compose.
        "build-ccf-containers",
        "build-azcliext-cleanroom",
    ],
    "test-ccf": [
        "build-ccr-proxy-containers",
        "build-cvm-containers",
        "build-ccr-skr-containers",
        "build-ccf-containers",
        # CCF tests deploy CGS (cgs-client, cgs-ui) via deploy-ccf.ps1.
        "build-cgs-containers",
        "build-azcliext-cleanroom",
    ],
    "test-cleanroom-cluster": [
        "build-ccr-governance-containers",
        "build-ccr-proxy-containers",
        "build-ccr-skr-containers",
        "build-cvm-containers",
        "build-k8s-node-containers",
        "build-ccr-containers",
        "build-cleanroom-cluster-containers",
        "build-cleanroom-operator-containers",
        "build-analytics-workload-containers",
        "build-kserve-inferencing-workload-containers",
        "build-frontend-service-containers",
        "build-azcliext-cleanroom",
    ],
    "test-multi-party-collab": [
        "build-ccr-governance-containers",
        "build-ccr-proxy-containers",
        "build-ccr-skr-containers",
        "build-ccr-tools-containers",
        # The CCF sandbox node requires cvm-attestation-verifier as a sidecar
        # to validate SEV-SNP attestation reports.
        "build-cvm-containers",
        "build-k8s-node-containers",
        "build-ccr-containers",
        # Multi-party-collab deploys CGS via deploy-cgs.ps1, which runs a CCF
        # sandbox node (ccf-runjs-app-sandbox, ccf-recovery-agent) and uses
        # CGS containers (cgs-client, cgs-ui).
        "build-ccf-containers",
        "build-cgs-containers",
        "build-azcliext-cleanroom",
    ],
    "test-workloads": [
        "build-ccr-governance-containers",
        "build-ccr-proxy-containers",
        "build-ccr-skr-containers",
        "build-cgs-containers",
        "build-ccr-tools-containers",
        "build-cvm-containers",
        "build-k8s-node-containers",
        "build-ccr-containers",
        "build-cleanroom-cluster-containers",
        "build-cleanroom-operator-containers",
        "build-analytics-workload-containers",
        "build-kserve-inferencing-workload-containers",
        "build-frontend-service-containers",
        "build-azcliext-cleanroom",
        "build-ccf-containers",
    ],
    # test-flex-node tests api-server-proxy which is part of build-k8s-node-containers.
    # It builds locally (no ACR dependency), so it doesn't need the build job.
    "test-flex-node": [
        "build-k8s-node-containers",
    ],
}

# Transitive build group dependencies derived from the `needs:` relationships
# between jobs in pr_build_all.yml. When a build group is enabled, all groups
# it transitively depends on must also be enabled (otherwise the downstream
# policy jobs that `needs:` both will be skipped).
# Format: group → set of groups it requires.
BUILD_GROUP_TRANSITIVE_DEPS = {
    "build-ccf-containers": {
        "build-ccr-governance-containers",
        "build-cvm-containers",
    },
    "build-ccr-containers": {
        "build-ccr-governance-containers",
        "build-ccr-proxy-containers",
        "build-ccr-skr-containers",
    },
    "build-analytics-workload-containers": {
        "build-ccr-governance-containers",
        "build-ccr-proxy-containers",
        "build-ccr-skr-containers",
        "build-ccr-containers",
    },
    "build-kserve-inferencing-workload-containers": {
        "build-ccr-governance-containers",
        "build-ccr-proxy-containers",
        "build-ccr-skr-containers",
        "build-ccr-containers",
    },
    "build-frontend-service-containers": {
        "build-ccr-governance-containers",
        "build-ccr-proxy-containers",
        "build-ccr-skr-containers",
        "build-cgs-containers",
    },
}


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

    1. A test stage is triggered when ANY of its dependency build groups has
       source changes. Once triggered, ALL build groups that test depends on
       are enabled so it has every container image it needs.
    2. Transitive build dependencies (BUILD_GROUP_TRANSITIVE_DEPS) are
       expanded last.
    """
    build_groups = dict(group_hits)

    # Determine which test stages are triggered — any dependency with direct
    # changes activates the test. Then ensure all its required groups are built.
    test_enabled: dict[str, bool] = {}
    for stage, required_groups in TEST_STAGE_GROUPS.items():
        triggered = any(group_hits.get(g, False) for g in required_groups)
        test_enabled[stage] = triggered
        if triggered:
            for req in required_groups:
                if not build_groups.get(req, False):
                    LOGGER.info("Enabling %s (runtime dependency of %s).", req, stage)
                    build_groups[req] = True

    # Apply transitive build dependencies.
    for group, deps in BUILD_GROUP_TRANSITIVE_DEPS.items():
        if build_groups.get(group, False):
            for dep in deps:
                if not build_groups.get(dep, False):
                    LOGGER.info(
                        "Enabling %s transitively (required by %s).", dep, group
                    )
                    build_groups[dep] = True

    # Build group outputs.
    outputs: dict[str, bool] = {}
    for group_name in GROUP_SCRIPTS:
        outputs[group_name] = build_groups.get(group_name, False)

    # Test stage outputs.
    for stage in TEST_STAGE_GROUPS:
        enabled = test_enabled[stage]
        outputs[f"run-{stage}"] = enabled
        LOGGER.info(
            "Test stage %s: %s (requires: %s)",
            stage,
            "enabled" if enabled else "skipped",
            ", ".join(sorted(TEST_STAGE_GROUPS[stage])),
        )

    return outputs


def write_outputs(outputs: dict[str, bool]) -> None:
    """Write outputs to GITHUB_OUTPUT as two comma-separated lists.

    Writes:
        build-groups — Comma-separated list of enabled build groups
                       (e.g., "build-ccr-governance-containers,build-cgs-containers").
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
