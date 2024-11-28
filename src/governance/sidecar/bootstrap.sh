#! /bin/bash

if [ "$INSECURE_VIRTUAL_ENVIRONMENT" != "true" ]; then
  # ccr-governance sidecar will generate a key pair and fetch the attestation report after launch.
  # So not setting ccrgovPrivKey/ccrgovPubKey/attestationReport/serviceCert env variables.

  # Wait for attestation sidecar to start as ccr-governance will call it.
  timeout 100 bash -c 'until ss -l -x | grep /mnt/uds/sock; do echo "Waiting for attestation-container..."; sleep 2; done'

  dotnet ./ccr-governance.dll
else
  echo "Running in insecure virtual mode. Picking keys/cert/report from $insecure_mountpoint and serviceCert from $serviceCertPath"
  cert="certs/ccr_gov_cert.pem"
  privk="keys/ccr_gov_priv_key.pem"
  pubk="keys/ccr_gov_pub_key.pem"
  attestationReport="attestation/attestation-report.json"

  export ccrgovPrivKey=$insecure_mountpoint$privk
  export ccrgovPubKey=$insecure_mountpoint$pubk
  export attestationReport=$insecure_mountpoint$attestationReport

  dotnet ./ccr-governance.dll
fi

