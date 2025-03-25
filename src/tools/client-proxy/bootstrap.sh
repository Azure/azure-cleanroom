#! /bin/bash

set -e

if [ -z "$CA_CERT" ]; then
    echo "Error: CA_CERT environment variable must be set"
    exit 1
fi

mkdir certs
ca_cert="certs/cleanroomca.crt"

echo "$CA_CERT" | base64 -d > $ca_cert

# Display the ca cert
openssl x509 -in "$ca_cert" -text -noout

export CLIENT_PROXY_PORT=${CLIENT_PROXY_PORT:-10080} 
cat ccr-client-proxy-config.yaml | envsubst '$CLIENT_PROXY_PORT' > /tmp/ccr-client-proxy-config.yaml

echo "Launching envoy"
exec envoy -c /tmp/ccr-client-proxy-config.yaml -l trace --log-path ccr-client-proxy.log