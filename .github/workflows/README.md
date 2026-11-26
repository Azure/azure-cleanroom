# Build-only GitHub proof of concept

`official_build_test.yml` builds the default output set of `official_build.yml` without
Azure authentication, registry publishing, or deployment. Existing workflows and Dockerfiles
are unchanged. All remote changes stay on the POC branch; no default-branch merge is needed.
The `id-token: write` permission is used only for GitHub provenance signing.

## Output coverage

`build/Get-OfficialBuildPlan.ps1` reads the official workflow's default build groups and
their composite actions, deduplicating shared images. It includes container images, five
Helm charts, the CGS JavaScript app and constitution, and the Azure CLI extension wheel.
Unsupported build definitions fail planning instead of silently omitting outputs.

Each output builds independently on the existing ephemeral 1ES pool, with at most four
parallel builds. Dockerfiles compile their own dependencies; redundant host .NET and
blobfuse compilation from the original workflow is not repeated. The cleanroom-client
image still builds its prerequisite CLI wheel. CCF image names and Docker targets match
the original actions, including their existing default-target behavior.

The original Dockerfiles reference the `cleanroombuild` ACR mirror for some base images.
Those references are not rewritten. If an anonymous pull is unavailable, the affected
build fails explicitly: this workflow does not acquire Azure credentials or silently
substitute a different base image. No Azure environment secrets are requested.

## Running

Pushes changing the POC workflow or its helpers on
`users/yavohra/github-build-only-cgs` trigger a full build. This narrowly scoped push
trigger permits testing before the workflow exists on the default branch.
Manual dispatch is available only once GitHub recognizes the workflow on the default
branch; do not merge merely to bootstrap the POC. Reusable workflow calls are also supported.

## Handoff and verification

Select an exact successful workflow run and source commit, not an unqualified latest run.
Artifacts are retained for 14 days:

- `official-build-plan`: all expected output definitions.
- `handoff-<name>-<commit>`: `<name>.tar.gz` and its `provenance.jsonl` signature bundle.
- `official-build-inventory-<commit>`: signed complete inventory, including GitHub artifact
  IDs, sizes, digests when returned by GitHub, and source/run identity.

Each output archive contains:

- `payload/`: a Docker-save image tar plus `image.json`, or the relevant files/chart/wheel.
- `sbom/_manifest/spdx_2.2/`: the SPDX SBOM and its generated integrity files.
- `source.json`: output name/kind, repository, commit, workflow identity, run and attempt.
- `SHA256SUMS`: checksums covering the payload, SBOM, and source metadata.

Container SBOMs scan the locally built image. Other SBOMs scan the delivered files and
dependencies detectable within them; opaque chart/wheel archives do not imply complete
transitive dependency enumeration. CGS app outputs include the resolved npm lockfile.
The workflow does not claim reproducible builds: existing builds use mutable base tags
and dependency resolution such as `npm install`.

Before extracting or publishing an output, verify its archive against the intended source
and signer. From the downloaded output directory, for example:

```bash
gh attestation verify identity.tar.gz \
  --repo Azure/azure-cleanroom \
  --signer-workflow Azure/azure-cleanroom/.github/workflows/official_build_test.yml \
  --source-digest <expected-full-commit-sha> \
  --bundle provenance.jsonl
mkdir verified
tar -xzf identity.tar.gz -C verified
(cd verified && sha256sum --check SHA256SUMS)
docker load --input verified/payload/identity.tar
```

Verify the signed inventory similarly, and confirm every expected output is present.
Confirm `source.json` matches the selected run and commit. After `docker load`, compare
the loaded image's config ID with `payload/image.json`. This ID is not a registry
manifest digest; record the final manifest digest separately when pushing to ACR.

The full-build gate fails if any planned output fails or any expected handoff artifact
is missing. Partial artifacts may remain for diagnosis but must not be promoted as a full build.

## Next stage: ADO and registry evidence

An ADO consumer in Azure-CleanRoomService must verify the exact successful run, signed
inventory, archives and checksums before publishing to a POC namespace in `cleanroomemubvt`.
SBOMs and provenance are sidecars, not automatically embedded in the image. `docker push`
alone does not publish them: explicitly attach evidence to the final image digest and
test registry-side discovery and retrieval. Validate preservation again through the
approved MCR promotion route. Creating or running that consumer is a separate step.
