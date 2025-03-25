#! /bin/bash
insecure_virtual_dir="/app/insecure-virtual/"
echo "Running in insecure virtual mode. Picking keys/cert/report from $insecure_virtual_dir"
privk="keys/ccr_gov_priv_key.pem"
pubk="keys/ccr_gov_pub_key.pem"
attestationReport="attestation/attestation-report.json"

export ccrgovPrivKey=$insecure_virtual_dir$privk
export ccrgovPubKey=$insecure_virtual_dir$pubk
export attestationReport=$insecure_virtual_dir$attestationReport

dotnet ./ccr-governance.dll