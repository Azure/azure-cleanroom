#! /bin/bash

# ccr-governance sidecar will generate a key pair and fetch the attestation report after launch.
# Wait for attestation sidecar to start as ccr-governance will call it.
timeout 100 bash -c 'until ss -l -x | grep /mnt/uds/sock; do echo "Waiting for attestation-container..."; sleep 2; done'
dotnet ./ccr-governance.dll