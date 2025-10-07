#! /bin/bash
set -e

LOCAL_CA="local"
CGS_CA="cgs"
SUPPORTED_CAS="$LOCAL_CA|$CGS_CA"

ca_type="cgs"

function usage()
{
    echo "Usage:"
    echo "  $0 [--ca-type [$CGS_CA|$LOCAL_CA]]"
    echo "Starts ccr-proxy for cleanroom HTTPS traffic handling."
    echo ""
    echo "Supported CA types are: $SUPPORTED_CAS. Defaults to cgs if not specified."
}

while [ "$1" != "" ]; do
    case $1 in
        -h|-\?|--help)
            usage
            exit 0
            ;;
        -c|--ca-type)
            ca_type="$2"
            shift
            ;;
        *)
            break
    esac
    shift
done

if ! [[ "$ca_type" =~ ^($SUPPORTED_CAS)$ ]]; then
    echo "$ca_type ca type is not in $SUPPORTED_CAS"
    exit 1
fi

# Generate self-signed certificate
mkdir -p keys
mkdir -p certs
out_key="keys/server-key.pem"
out_cert="certs/server-cert.pem"

if [ "$ca_type" = "$CGS_CA" ]; then

    san=\"dNSName:*.cleanroom.local\"
    if [ -n "${CCR_FQDN}" ]; then
        san=$san,\"dNSName:${CCR_FQDN}\"
    fi

    echo "Generating SSL certificate using CGS CA."
    GOVERNANCE_PORT=${GOVERNANCE_PORT:-8300}
    ./wait-for-it.sh --timeout=100 --strict 127.0.0.1:${GOVERNANCE_PORT} -- echo "Governance sidecar available"

    ## 1. Create the certificate's private/public key
    openssl ecparam -name secp384r1 -genkey -noout -out $out_key
    openssl ec -in $out_key -pubout > certs/ec-secp384r1-pub-key.pem

    ## 2. Generate the certificate endorsed with the CA cert in CGS.
    # Convert pem into single-line strings for JSON payloads.
    publicKeyPem=$(awk 'NF {sub(/\r/, ""); printf "%s\\n",$0;}' certs/ec-secp384r1-pub-key.pem)
    cat > request.json <<EOF
{"publicKey": "$publicKeyPem", "subjectName": "CN=ccr-proxy", "validityPeriodDays": 100, "subjectAlternateNames":[$san]}
EOF
    echo "Sending cert generation request with payload: "
    cat request.json | jq
    curl --fail-with-body -X POST "127.0.0.1:${GOVERNANCE_PORT}/ca/generateEndorsedCert" -s -S -H 'Content-Type: application/json' -d @request.json -o ./response.json || {
        code=$?
        echo "curl exited with code: $code with response:"
        # Pretty print the response if it is a valid JSON, otherwise just print it as is.
        if jq . ./response.json >/dev/null 2>&1; then
            jq . ./response.json
        else
            cat ./response.json
        fi
        exit $code
    }
    cat ./response.json | jq -r .cert > $out_cert
else
    san="IP:0.0.0.0,IP:127.0.0.1,DNS:*.cleanroom.local,DNS:localhost"
    if [ -n "${CCR_FQDN}" ]; then
        san="${san},DNS:${CCR_FQDN}"
    fi

    export san
    cat ./cleanroom.cnf | envsubst '$san' > /tmp/cert-config.cnf
    echo "Generating SSL certificate using local CA."

    # Create clean room CA cert
    ## 1. Create the root key
    openssl genrsa -out keys/CA.key 2048
    ## 2. Create a Root Certificate and self-sign it
    openssl req -x509 -new -nodes -key keys/CA.key -sha256 -days 100 -out certs/CA.crt -config /tmp/cert-config.cnf -extensions v3_ca -subj "/CN=Azure Clean Room CA"

    # Create clean room cert
    ## 1. Create the certificate's key
    openssl genrsa -out "$out_key" 2048
    
    ## 2. Create the CSR (Certificate Signing Request)
    openssl req -new -key "$out_key" -out cleanroom.csr -config /tmp/cert-config.cnf -extensions v3_req
    
    ## 3. Generate the certificate with the CSR and the key and sign it with the CA's root key
    openssl x509 -req -in cleanroom.csr -CA certs/CA.crt -CAkey keys/CA.key -CAcreateserial -out "$out_cert" -days 100 -sha256 -extfile /tmp/cert-config.cnf -extensions v3_req
fi

# Display the created cert
openssl x509 -in "$out_cert" -text -noout

cat ccr-proxy-config.yaml > /tmp/ccr-proxy-config.yaml
mkdir -p /var/lib/envoy
echo "Converting templates to generate listener and cluster configurations..."
python3 render-config/render_config.py --output-path /var/lib/envoy --template-path templates

echo "Starting envoy with the following configuration:"
cat /tmp/ccr-proxy-config.yaml
echo "Listener configuration...."
cat /var/lib/envoy/lds.yaml
echo "Cluster configuration...."
cat /var/lib/envoy/cds.yaml

echo "Launching envoy"
# Use exec so that SIGTERM is propagated to the child process and the process can be gracefully stopped.
exec envoy -c /tmp/ccr-proxy-config.yaml -l trace --log-path $TELEMETRY_MOUNT_PATH/infrastructure/ccr-proxy.log