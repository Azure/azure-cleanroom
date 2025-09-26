#! /bin/bash
insecure_virtual_dir=${INSECURE_VIRTUAL_DIR:-"/app/insecure-virtual/"}
echo "Running in insecure virtual mode. Picking keys/report from $insecure_virtual_dir"
privk="keys/ccr_gov_priv_key.pem"
pubk="keys/ccr_gov_pub_key.pem"
attestationReport="attestation/attestation-report.json"

export ccrgovPrivKey=$insecure_virtual_dir$privk
export ccrgovPubKey=$insecure_virtual_dir$pubk
export attestationReport=$insecure_virtual_dir$attestationReport

# Use exec so that SIGTERM is propagated to the child process and the process can be gracefully stopped.
exec dotnet ./ccr-governance.dll