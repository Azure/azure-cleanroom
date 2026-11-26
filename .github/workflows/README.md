# Build-only GitHub proof of concept

`official_build_test.yml` builds the CGS JavaScript app and constitution without Azure
authentication, registry publishing, or deployment. Existing workflows are unchanged.
It uses a GitHub-hosted Ubuntu runner, with no environment secrets or Corp identity.
Its `id-token: write` permission is used only for GitHub provenance signing.

The app uses the existing Dockerfile's `build-image` stage so the handoff can include
the exact npm lockfile generated during the build as well as the release bundle.
The constitution uses the existing composite action. This is a build proof of concept,
not the full official build or Azure integration-test coverage.

## Running

The initial push to `users/yavohra/github-build-only-cgs` runs this workflow when its YAML
changes. This narrow trigger permits testing before the workflow exists on the default
branch. After merging, use `workflow_dispatch`, or call it as a reusable workflow.
Remove the bootstrap push trigger when branch testing is complete.

## Handoff and verification

Download `cgs-build-<commit>` from the exact successful workflow run within its 14-day
retention period. It contains:

- `cgs-build.tar.gz`: app `bundle.json`, `constitution.json`, npm manifests,
  `source.json`, an SPDX 2.2 SBOM under `_manifest/`, and `SHA256SUMS`.
- `provenance.jsonl`: GitHub build provenance attesting the archive's SHA-256.

Before any publishing, authenticate the archive against the intended repository,
workflow and source commit, not just its checksums. For example, from the download directory:

```bash
gh attestation verify cgs-build.tar.gz \
  --repo Azure/azure-cleanroom \
  --signer-workflow Azure/azure-cleanroom/.github/workflows/official_build_test.yml \
  --source-digest <expected-full-commit-sha> \
  --bundle provenance.jsonl
mkdir verified
tar -xzf cgs-build.tar.gz -C verified
(cd verified && sha256sum --check SHA256SUMS)
```

Confirm `source.json` matches the selected run and commit. The npm lockfile records the
resolved build dependencies; the SBOM is not a container-image SBOM. The existing
Dockerfile uses `npm install` and mutable base tags, so this workflow does not claim
reproducible builds.

An Azure DevOps consumer must select a trusted successful run and perform these
verification steps before publishing. Creating that consumer, registry promotion,
and Azure-dependent testing are outside this proof of concept.
