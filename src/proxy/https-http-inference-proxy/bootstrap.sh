#!/bin/bash
set -e

# Bootstrap script for inference proxy mode. Delegates TLS certificate generation
# to the base https-http/bootstrap.sh logic, then configures Envoy with ext_authz,
# Lua-based host rewriting, and dynamic forward proxy for routing inference requests
# to predictor pods over TLS.
#
# Required env vars:
#   CCR_ENVOY_DESTINATION_PORT  — port of the local inferencing-agent (e.g. 8080)
#
# Optional env vars:
#   CCR_ENVOY_ADDITIONAL_ROUTES — comma-separated "prefix:port" for extra local routes
#   GOVERNANCE_PORT             — governance sidecar port (default: 8300)

LOCAL_CA="local"
CGS_CA="cgs"
SUPPORTED_CAS="$LOCAL_CA|$CGS_CA"

ca_type=""

function usage()
{
    echo "Usage:"
    echo "  $0 --ca-type [$CGS_CA|$LOCAL_CA]"
    echo "Starts ccr-proxy in inference proxy mode."
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

# Generate TLS certificates — same logic as https-http/bootstrap.sh.
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
    cat https-http/cert-config.cnf | envsubst '$san' > /tmp/cert-config.cnf

    if [[ "$GENERATE_CA_CERT" == "true" ]]; then
        echo "Generating local CA SSL certificate with subj $CCR_ENVOY_CERT_SUBJECT_NAME"

        openssl genrsa -out "$ca_key" 2048
        openssl req -x509 -new -nodes -key "$ca_key" -sha256 -days 100 \
            -out "$ca_cert" -config /tmp/cert-config.cnf \
            -extensions v3_ca -subj "/$CCR_ENVOY_CERT_SUBJECT_NAME"

        if [ -n "$CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE" ]; then
            echo "Writing CA cert to $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE"
            mkdir -p "$(dirname $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE)"
            cat $ca_cert > $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE
        fi
    fi

    openssl genrsa -out "$out_key" 2048
    openssl req -new -key "$out_key" -out cert-config.csr \
        -config /tmp/cert-config.cnf -extensions v3_req
    openssl x509 -req -in cert-config.csr -CA "$ca_cert" -CAkey "$ca_key" \
        -CAcreateserial -out "$out_cert" -days 100 -sha256 \
        -extfile /tmp/cert-config.cnf -extensions v3_req
else
    echo "Generating SSL certificate using CGS CA with subj $CCR_ENVOY_CERT_SUBJECT_NAME"
    GOVERNANCE_PORT=${GOVERNANCE_PORT:-8300}
    ./wait-for-it.sh --timeout=100 --strict 127.0.0.1:${GOVERNANCE_PORT} \
        -- echo "Governance sidecar available"

    san=\"dNSName:*.cleanroom.local\"
    if [ -n "${CCR_FQDN}" ]; then
        san=$san,\"dNSName:${CCR_FQDN}\"
    fi

    openssl ecparam -name secp384r1 -genkey -noout -out $out_key
    openssl ec -in $out_key -pubout > certs/ec-secp384r1-pub-key.pem

    publicKeyPem=$(awk 'NF {sub(/\r/, ""); printf "%s\\n",$0;}' certs/ec-secp384r1-pub-key.pem)
    cat > request.json <<EOF
{"publicKey": "$publicKeyPem", "subjectName": "$CCR_ENVOY_CERT_SUBJECT_NAME", "validityPeriodDays": 100, "subjectAlternateNames":[$san]}
EOF
    echo "Sending cert generation request with payload: "
    cat request.json | jq
    curl --fail-with-body -X POST \
        "127.0.0.1:${GOVERNANCE_PORT}/ca/generateEndorsedCert" \
        -s -S -H 'Content-Type: application/json' -d @request.json \
        -o ./response.json || {
        code=$?
        echo "curl exited with code: $code with response:"
        if jq . ./response.json >/dev/null 2>&1; then
            jq . ./response.json
        else
            cat ./response.json
        fi
        exit $code
    }
    cat ./response.json | jq -r .cert > $out_cert

    if [ -n "$CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE" ]; then
        mkdir -p "$(dirname $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE)"
        cat $out_cert > $CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE
    fi
fi

openssl x509 -in "$out_cert" -text -noout

# Fetch CA cert for upstream TLS to predictor pods.
if [ "$ca_type" = "$CGS_CA" ]; then
    echo "Fetching CGS CA cert for upstream predictor TLS..."
    GOVERNANCE_PORT=${GOVERNANCE_PORT:-8300}
    ./wait-for-it.sh --timeout=100 --strict 127.0.0.1:${GOVERNANCE_PORT} \
        -- echo "Governance sidecar available for CA cert fetch"
    curl -sf "http://127.0.0.1:${GOVERNANCE_PORT}/ca/info" \
        | jq -r '.caCert' > certs/upstream-ca.pem
    if [ ! -s certs/upstream-ca.pem ]; then
        echo "Error: Failed to fetch CGS CA cert from governance sidecar."
        exit 1
    fi
    echo "CGS CA cert fetched successfully."
else
    echo "Using local CA cert for upstream predictor TLS..."
    cp "$ca_cert" certs/upstream-ca.pem
    echo "Local CA cert copied successfully."
fi

# Template the Envoy config.
export CCR_ENVOY_LISTENER_HTTPS_ENDPOINT=${CCR_ENVOY_LISTENER_HTTPS_ENDPOINT:-"0.0.0.0"}
export CCR_ENVOY_LISTENER_HTTPS_PORT=${CCR_ENVOY_LISTENER_HTTPS_PORT:-443}
export CCR_ENVOY_DESTINATION_ENDPOINT=${CCR_ENVOY_DESTINATION_ENDPOINT:-"0.0.0.0"}
cat https-http-inference-proxy/envoy-config.yaml | envsubst \
    '$CCR_ENVOY_LISTENER_HTTPS_ENDPOINT $CCR_ENVOY_LISTENER_HTTPS_PORT $CCR_ENVOY_DESTINATION_ENDPOINT $CCR_ENVOY_DESTINATION_PORT' \
    > /tmp/ccr-inference-proxy-config.yaml

# Inject additional routes and clusters if CCR_ENVOY_ADDITIONAL_ROUTES is set.
# Format: comma-separated "prefix:port" entries, e.g. "/ohttp-gateway:8090".
# Each entry adds a prefix-matched route (before the catch-all "/" route) and a
# corresponding STATIC cluster pointing to 0.0.0.0:port. ext_authz is enabled
# so the caller is authenticated before the request reaches the backend.
if [ -n "$CCR_ENVOY_ADDITIONAL_ROUTES" ]; then
    echo "Injecting additional routes: $CCR_ENVOY_ADDITIONAL_ROUTES"
    config="/tmp/ccr-inference-proxy-config.yaml"
    IFS=',' read -ra ROUTES <<< "$CCR_ENVOY_ADDITIONAL_ROUTES"
    for route_entry in "${ROUTES[@]}"; do
        prefix="${route_entry%%:*}"
        port="${route_entry##*:}"
        cluster_name="route_$(echo "$prefix" | tr -d '/')"

        echo "  Adding route: prefix='${prefix}' -> cluster='${cluster_name}' port=${port}"

        route_block=$(cat <<ROUTE
              - match:
                  prefix: "${prefix}"
                route:
                  cluster: ${cluster_name}
                  timeout: 360s
ROUTE
        )

        python3 -c "
import sys
block = sys.argv[1]
lines = open(sys.argv[2]).readlines()
out = []
inserted = False
for i, line in enumerate(lines):
    if (not inserted
        and '- match:' in line
        and i + 1 < len(lines)
        and 'prefix: \"/\"' in lines[i + 1]):
        out.append(block + '\n')
        inserted = True
    out.append(line)
open(sys.argv[2], 'w').writelines(out)
" "$route_block" "$config"

        cat >> "$config" <<EOF
  - name: ${cluster_name}
    type: STATIC
    connect_timeout: 5s
    load_assignment:
      cluster_name: ${cluster_name}
      endpoints:
      - lb_endpoints:
        - endpoint:
            address:
              socket_address:
                address: 0.0.0.0
                port_value: ${port}
EOF
    done

    echo "Final envoy config:"
    cat "$config"
fi

echo "Launching envoy in inference proxy mode"
exec envoy -c /tmp/ccr-inference-proxy-config.yaml
