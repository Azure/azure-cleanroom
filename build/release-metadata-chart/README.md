# release-metadata

A versioned **release metadata** package for cleanroom, distributed as a Helm
chart purely to reuse Helm's repository infrastructure (catalog `index.yaml`,
versioning, packaging, discovery). It is **not a deployable chart**.

- `type: library` — Helm refuses `helm install` / `helm template`.
- There are **no Kubernetes manifests**. `values.yaml` is the payload.
- Consumers read it with `helm show values` / `helm pull`, **never** `helm install`.

## Contract (`values.yaml`)

`values.yaml` is the API document, validated by `values.schema.json`:

```yaml
apiVersion: metadata.cleanroom.azure.com/v1
kind: ReleaseMetadata
metadata:
  release: 8.0.0
  published: "2026-07-16"
images:
  <logical-name>: <registry/path@sha256:...>        # digest-pinned container image
helm-charts:
  <logical-name>: oci://<registry/path>:tag          # OCI chart tag
artefacts:
  <logical-name>: <registry/path:tag>               # security-policy docs, js apps,
                                                     # constitutions, CLI wheels (tag-referenced)
```

The catalog is a **flat list separated by kind**: `images` (digest-pinned `@sha256:`),
`helm-charts` (`oci://…:tag`), and `artefacts` (`…:tag` — security-policy documents,
`cgs-js-app`, `cgs-constitution`, the CLI wheel). Logical names are unique **within a
kind**; images and charts deliberately reuse names (e.g. `frontend-service` is both an
image and a chart), which the separate kind buckets keep apart. The CCF provider reader
merges `images` + `artefacts` by logical name and looks up the references it needs.

`apiVersion`/`kind` here are the *domain contract* identifiers and are unrelated
to Helm's own `Chart.yaml` `apiVersion`/`type`. Unknown fields are ignored and
reserved for future contract growth.

## Versioning

- `Chart.yaml` `version` — the release/catalog version (drives the `.tgz` name
  and `index.yaml` entry). Mirrored in `metadata.release`.

The image references in `values.yaml` move on their own cadence and are not
tied to the chart version.

## Consuming

```bash
helm repo add release-metadata <repo-base-url>
helm search repo release-metadata --versions          # discover versions
helm show values release-metadata/release-metadata --version 1.0.0
helm pull release-metadata/release-metadata --version 1.0.0
```

## Producing

The committed `values.yaml` is a **generated sample**.
`build/build-release-metadata-chart.ps1` regenerates it at release time from a
flat in-script catalog (kind → logical name → repo path): it resolves image
digests, emits the flat `values.yaml`, `helm lint`s it against
`values.schema.json`, `helm package`s the chart, and (in CI) chart-releaser owns
`index.yaml`. **Every release is a full rebuild** — all artefacts are rebuilt and
published at the release tag, so the catalog is always a complete snapshot resolved
fresh at `$tag`. There are no partial releases and nothing is carried forward. The
`-releasedGroups` / `-previousIndexUrl` parameters are retained (ignored) only for
backward compatibility with existing pipeline callers and are removed with the
release-type cleanup.

## Relationship to `versions/*` and `sidecar-digests` (interim)

The standalone `versions/<component>` documents and `sidecar-digests` are **not**
catalog entries. Their data (image digests, policy references) is already expressed
natively under the flat `images` / `artefacts` kinds, so the catalog does not point
at those documents.

During the interim they are **still published in parallel** and remain the live
source for existing consumers (az CLI `helpers.py`/`custom.py`, the cleanroom-cluster
providers, and CI attestation verification), which still `oras pull` and parse their
shape. They are retired only once those consumers are migrated to read this catalog,
and once the catalog carries the last gaps those consumers depend on (policy
`@digest`, the `ccr-proxy-https-http` sidecar variant, and a per-component version for
digest→version reverse lookup).
