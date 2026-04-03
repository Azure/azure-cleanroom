#! /bin/bash
auth_mode=${ccrgovAuthMode:-"SnpAttestation"}
echo "AuthMode is $auth_mode."
if [ "$auth_mode" = "SnpAttestation" ]; then
    insecure_virtual_dir=${INSECURE_VIRTUAL_DIR:-"/app/insecure-virtual/"}
    echo "Running in insecure virtual mode. Picking keys/report from $insecure_virtual_dir"
    attestationReport="encryption/attestation.json"
    export attestationReport=$insecure_virtual_dir$attestationReport
fi

# Use exec so that SIGTERM is propagated to the child process and the process can be gracefully stopped.
exec dotnet ./ccr-governance.dll