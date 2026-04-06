#! /bin/bash
auth_mode=${ccrgovAuthMode:-"SnpAttestation"}
echo "AuthMode is $auth_mode."
if [ "$auth_mode" = "SnpAttestation" ]; then
    # ccr-governance sidecar will generate a key pair and fetch the attestation report after launch.
    # Wait for the attestation sidecar to start as ccr-governance will call it.
    # SEV-SNP (CACI/VN2) uses skr on port 8284, CVM uses cvm-attestation-agent on port 8900.
    if [ -e "/dev/sev" ] || [ -e "/dev/sev-guest" ]; then
        AGENT_PORT=${SKR_PORT:-8284}
    else
        AGENT_PORT=${CVM_ATTESTATION_AGENT_PORT:-8900}
    fi
    ./wait-for-it.sh --timeout=100 --strict 127.0.0.1:${AGENT_PORT} -- echo "Attestation sidecar available on port ${AGENT_PORT}"
fi

# Use exec so that SIGTERM is propagated to the child process and the process can be gracefully stopped.
exec dotnet ./ccr-governance.dll