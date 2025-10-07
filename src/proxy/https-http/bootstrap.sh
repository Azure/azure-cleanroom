#! /bin/bash
set -e

LOCAL_CA="local"
CGS_CA="cgs"
SUPPORTED_CAS="$LOCAL_CA|$CGS_CA"

ca_type=""

function usage()
{
    echo "Usage:"
    echo "  $0 --ca-type [$CGS_CA|$LOCAL_CA]"
    echo "Starts ccr-proxy for HTTPS->HTTP traffic handling."
    echo ""
    echo "Supported CA types are: $SUPPORTED_CAS"
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

if [ -z "$CCR_ENVOY_DESTINATION_PORT" ]; then
    echo "Error: CCR_ENVOY_DESTINATION_PORT environment variable must be set"
    exit 1
fi

CCR_ENVOY_CERT_SUBJECT_NAME=${CCR_ENVOY_CERT_SUBJECT_NAME:-"CN=CCR CA"}

# Generate self-signed certificate
BASEDIR=$(dirname "$0")
mkdir -p keys
mkdir -p certs
out_key="keys/server-key.pem"
out_cert="certs/server-cert.pem"

if [ "$ca_type" = "$LOCAL_CA" ]; then
    CCR_LOCAL_CA_CERT_OUT_DIR=${CCR_LOCAL_CA_CERT_OUT_DIR:-$BASEDIR}
    echo "Using $CCR_LOCAL_CA_CERT_OUT_DIR as local CA cert output directory..."
    mkdir -p $CCR_LOCAL_CA_CERT_OUT_DIR/keys
    mkdir -p $CCR_LOCAL_CA_CERT_OUT_DIR/certs
    ca_cert="$CCR_LOCAL_CA_CERT_OUT_DIR/certs/CA.crt"
    ca_key="$CCR_LOCAL_CA_CERT_OUT_DIR/keys/CA.key"
    
    GENERATE_CA_CERT=true
    if [ -f "$ca_cert" ]; then
        echo "$ca_cert already exists, skipping generation of CA certificate."
        GENERATE_CA_CERT=false
    fi

    san="IP:0.0.0.0,IP:127.0.0.1,DNS:*.cleanroom.local,DNS:localhost"
    if [ -n "${CCR_FQDN}" ]; then
        san="${san},DNS:${CCR_FQDN}"
    fi
    export san
    cat $BASEDIR/cert-config.cnf | envsubst '$san' > /tmp/cert-config.cnf

    if [[ "$GENERATE_CA_CERT" == "true" ]]; then
        echo "Generating local CA SSL certificate with subj $CCR_ENVOY_CERT_SUBJECT_NAME and config file $(cat /tmp/cert-config.cnf)"

        # Create CA cert
        ## 1. Create the root key
        openssl genrsa -out "$ca_key" 2048
        ## 2. Create a Root Certificate and self-sign it
        openssl req -x509 -new -nodes -key "$ca_key" -sha256 -days 100 -out "$ca_cert" -config /tmp/cert-config.cnf -extensions v3_ca -subj "/$CCR_ENVOY_CERT_SUBJECT_NAME"

        # As local CA is used writing out the CA cert and key to the directory so that it can be used for SSL cert
        # validation as the trusted root cert.
        if [ -n "$CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE" ]; then
            echo "Writing CA cert to $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE"
            mkdir -p "$(dirname $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE)"
            cat $ca_cert > $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE
        fi
    fi

    # Create the node cert
    ## 1. Create the certificate's key
    openssl genrsa -out "$out_key" 2048

    ## 2. Create the CSR (Certificate Signing Request)
    openssl req -new -key "$out_key" -out cert-config.csr -config /tmp/cert-config.cnf -extensions v3_req

    ## 3. Generate the certificate with the CSR and the key and sign it with the CA's root key
    openssl x509 -req -in cert-config.csr -CA "$ca_cert" -CAkey "$ca_key" -CAcreateserial -out "$out_cert" -days 100 -sha256 -extfile /tmp/cert-config.cnf -extensions v3_req
else
    echo "Generating SSL certificate using CGS CA with subj $CCR_ENVOY_CERT_SUBJECT_NAME"
    GOVERNANCE_PORT=${GOVERNANCE_PORT:-8300}
    ./wait-for-it.sh --timeout=100 --strict 127.0.0.1:${GOVERNANCE_PORT} -- echo "Governance sidecar available"

    san=\"dNSName:*.cleanroom.local\"
    if [ -n "${CCR_FQDN}" ]; then
        san=$san,\"dNSName:${CCR_FQDN}\"
    fi

    ## 1. Create the certificate's private/public key
    openssl ecparam -name secp384r1 -genkey -noout -out $out_key
    openssl ec -in $out_key -pubout > certs/ec-secp384r1-pub-key.pem

    ## 2. Generate the certificate endorsed with the CA cert in CGS.
    # Convert pem into single-line strings for JSON payloads.
    publicKeyPem=$(awk 'NF {sub(/\r/, ""); printf "%s\\n",$0;}' certs/ec-secp384r1-pub-key.pem)
    cat > request.json <<EOF
{"publicKey": "$publicKeyPem", "subjectName": "$CCR_ENVOY_CERT_SUBJECT_NAME", "validityPeriodDays": 100, "subjectAlternateNames":[$san]}
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

    # As CGS CA is used writing out the service cert to the file as CA cert is available directly
    # from CGS endpoint itself which can be used for SSL cert validation as the trusted root cert.
    if [ -n "$CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE" ]; then
        mkdir -p "$(dirname $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE)"
        cat $out_cert > $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE
    fi
fi

# Display the created cert
openssl x509 -in "$out_cert" -text -noout

export CCR_ENVOY_LISTENER_HTTPS_ENDPOINT=${CCR_ENVOY_LISTENER_HTTPS_ENDPOINT:-"0.0.0.0"}
export CCR_ENVOY_LISTENER_HTTPS_PORT=${CCR_ENVOY_LISTENER_HTTPS_PORT:-443}
export CCR_ENVOY_CLUSTER_TYPE=${CCR_ENVOY_CLUSTER_TYPE:-"STATIC"}
export CCR_ENVOY_DESTINATION_ENDPOINT=${CCR_ENVOY_DESTINATION_ENDPOINT:-"0.0.0.0"}

cat https-http/ccr-https-http-proxy-config.yaml | envsubst \
    '$CCR_ENVOY_LISTENER_HTTPS_ENDPOINT $CCR_ENVOY_LISTENER_HTTPS_PORT $CCR_ENVOY_CLUSTER_TYPE $CCR_ENVOY_DESTINATION_ENDPOINT $CCR_ENVOY_DESTINATION_PORT' \
    > /tmp/ccr-https-http-proxy-config.yaml

echo "Launching envoy"
# Use exec so that SIGTERM is propagated to the child process and the process can be gracefully stopped.
exec envoy -c /tmp/ccr-https-http-proxy-config.yaml