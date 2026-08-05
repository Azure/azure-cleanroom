# TPC-DS Cleanroom Analytics — Stress Testing

[TPC-DS](https://www.tpc.org/tpcds/) is the industry-standard benchmark for
SQL-at-scale decision-support workloads (24 tables, 99 queries, scale factors
from 1 GB to 100 TB). This harness runs TPC-DS queries through the cleanroom
Spark stack end-to-end.

The dataset must be generated ad-hoc for each scale factor using `dsqgen`
(no pre-built datasets above ~1 GB exist publicly). Persistent storage cost
is **~$50/month** (Azure Blob, Hot tier) across publisher + consumer
accounts; re-generating SF1000 costs more in Azure VM compute
plus blob-write transactions.

### Where things live

| Path                                                       | What it is                                              |
|------------------------------------------------------------|---------------------------------------------------------|
| [`stresstest/runner/`](runner/)                            | E2E driver — Python entrypoint + PowerShell scenarios   |
| [`stresstest/data_prep/`](data_prep/)                      | One-off VM-based dataset generation (`dsqgen` wrapper)  |
| [`stresstest/fixtures/`](fixtures/)                        | Committed query SQL + table-partition config            |
| [`stresstest/analysis/`](analysis/)                        | CI verification gate + failure summary reports          |
| [`stresstest/lib/`](lib/)                                  | Shared helpers used by `runner/` and `analysis/`        |

## Prerequisites

- TPC-DS data uploaded to publisher/consumer storage accounts (already done
  for the shared accounts — see [Pre-loaded Datasets](#pre-loaded-datasets)).
- Query SQL is committed under [`stresstest/fixtures/queries/`](fixtures/queries/),
  so [`runner/run-scenario.ps1`](runner/run-scenario.ps1) needs no extra tools
  to run against pre-loaded data.

> Generating fresh data for a new SF additionally requires the TPC-DS toolkit
> — see [Generating Fresh Data](#generating-fresh-data).

## Setup (AKS)

```powershell
az login
az acr update --name <acr-name> --anonymous-pull-enabled true
az acr login --name <acr-name>
pwsh ./build/onebox/build-containers.ps1 -repo <acr-name>.azurecr.io -tag latest -withRegoPolicy
pwsh test/onebox/workloads/setup-env.ps1 -infraType aks -registry acr -repo <acr-name>.azurecr.io -maxWorkers 4 -enableMonitoring
```

## Running the E2E Test

### Local (AKS)

Two stages: [`run-scenario.ps1`](runner/run-scenario.ps1) does cleanroom setup
(datastores, contract, CA, deployment, cluster) and writes
`submitSqlJobConfig.json`; [`submit_tpcds_job.py`](runner/submit_tpcds_job.py)
reads it and submits queries. [`run-local.ps1`](runner/run-local.ps1) wraps
both. Assumes [`setup-env.ps1`](../../../workloads/setup-env.ps1) has been run.

```bash
source .venv/bin/activate
pip3 install -r test/onebox/multi-party-collab/analytics-load-testing-TPC-DS/stresstest/requirements.txt

cd test/onebox/multi-party-collab/analytics-load-testing-TPC-DS

# Fresh setup + workload (defaults: SF=10, iterations=2, parallel=5)
pwsh stresstest/runner/run-local.ps1

# Re-submit only, reuses prior setup
pwsh stresstest/runner/run-local.ps1 -skipSetup -iterations 2 -parallel 5
```

[`analysis/verify_run.py`](analysis/verify_run.py) is the CI gate.

### CI (GitHub Actions)

Workflow: **`TPC-DS Analytics Stresstest`**
([`.github/workflows/tpcds-stresstest.yml`](../../../../../.github/workflows/tpcds-stresstest.yml)).

 **Schedule** — one scale factor per weekday at 06:00 UTC (Mon=10, Tue=100,
  Wed=400, Thu=700, Fri=1000). Scheduled runs always use the version of the
  workflow on the repo's **default branch**.


**Iterating on a custom branch (auto-run on push):** the workflow has a
commented-out `push` trigger at the top of
[`tpcds-stresstest.yml`](../../../../../.github/workflows/tpcds-stresstest.yml).
Uncomment it and set your branch name — then every push to that branch runs
the workflow. Re-comment it before
merging so the workflow only runs on schedule + manual dispatch:

## Pre-loaded Datasets

TPC-DS data is **already generated** at SF **10, 100, 400, 700, 1000** (csv, parquet) in two storage accounts:

| Role      | Resource group                              | Storage account     |
|-----------|---------------------------------------------|---------------------|
| Publisher | `cl-ob-publisher-tpcds-analytics` | `avwgndilajulqsa`   |
| Consumer  | `cl-ob-consumer-tpcds-analytics`  | `nldjeffcxauamsa`   |

Both RGs are tagged **`SkipCleanup=true`** so the subscription cleanup job
that prunes idle resource groups skips them. **Do not remove this tag** —
re-generating SF1000 takes hours of Azure VM compute and several GB of
blob-write transactions.

## Generating Fresh Data

Only needed for a new scale factor or a wiped account. Script:
[`data_prep/generate-tpcds-on-azure-vm.ps1`](data_prep/generate-tpcds-on-azure-vm.ps1).

**Toolkit setup (one-time):** the **TPC-DS toolkit** (`DSGen-software-code-4.0.0/`)
must sit alongside `stresstest/`. It cannot be redistributed — download
from <https://www.tpc.org/tpc_documents_current_versions/current_specifications5.asp>
("TPC-DS Tools" link, requires accepting the TPC license), then unzip into
`test/onebox/multi-party-collab/analytics-load-testing-TPC-DS/DSGen-software-code-4.0.0/`.

```powershell
pwsh stresstest/data_prep/generate-tpcds-on-azure-vm.ps1 `
    -scaleFactor 1000 `
    -publisherStorageAccount <storage-account> -consumerStorageAccount <storage-account> `
    -publisherResourceGroup <rg> -consumerResourceGroup <rg>
```

## Reference

### Query Profiles

SQL query lives under [`fixtures/queries/`](fixtures/queries/).

| Query     | Category                        | SQL                                                  |
|-----------|---------------------------------|------------------------------------------------------|
| `query1`  | scan_heavy                      | [`query1.sql`](fixtures/queries/query1.sql)          |
| `query14` | join_heavy (split into 14a/14b) | [`query14a.sql`](fixtures/queries/query14a.sql)      |
| `query24` | aggregation_heavy               | [`query24a.sql`](fixtures/queries/query24a.sql)      |
| `query64` | complex_joins                   | [`query64.sql`](fixtures/queries/query64.sql)        |
| `query72` | large_shuffle                   | [`query72.sql`](fixtures/queries/query72.sql)        |

### Table Partitioning

24 TPC-DS tables split between publisher (12 fact) and consumer (12
dimension). Each table → own blob container:
`tpcds-{pub,con}-{table}-sf{N}-{format}`. Config:
[`fixtures/table-partition-config.json`](fixtures/table-partition-config.json).

## Observations & Tuning Notes

Known failure modes, memory/disk spill behaviour, and the shuffle-partition
(200 vs 2000) tuning study have moved to a dedicated document:
[`OBSERVATIONS.md`](OBSERVATIONS.md).