#!/usr/bin/env bash
set -euo pipefail

# policy-signing-tool.sh — Local signing utility using openssl CLI.
#
# Generates an RSA-2048 key pair + self-signed certificate and signs base64-encoded
# payloads using RSA-PSS with SHA-256 (salt length = hash length).
#
# Usage:
#   policy-signing-tool.sh generate [--key-dir <dir>]
#   policy-signing-tool.sh sign     [--key-dir <dir>] <base64-payload>
#   policy-signing-tool.sh cert     [--key-dir <dir>]
#
# Commands:
#   generate  Create a new RSA-2048 private key and self-signed certificate.
#   sign      Sign a base64-encoded payload (RSA-PSS, SHA-256). Prints the
#             base64-encoded signature to stdout.
#   cert      Print the path to the signing certificate PEM file.
#
# Options:
#   --key-dir <dir>   Directory for key material (default: ./tmp/policy-signing-keys)
#
# The generated files are:
#   <key-dir>/signing-key.pem    RSA private key (PEM)
#   <key-dir>/signing-cert.pem   Self-signed certificate (PEM)

KEY_DIR="./tmp/policy-signing-keys"

usage() {
    head -26 "$0" | grep -E "^#" | sed 's/^# \?//'
    exit 1
}

# Parse global options before command
while [[ $# -gt 0 ]]; do
    case "$1" in
        --key-dir)
            KEY_DIR="$2"
            shift 2
            ;;
        generate|sign|cert)
            COMMAND="$1"
            shift
            break
            ;;
        -h|--help)
            usage
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage
            ;;
    esac
done

# Also allow --key-dir after command
while [[ $# -gt 0 ]]; do
    case "$1" in
        --key-dir)
            KEY_DIR="$2"
            shift 2
            ;;
        -*)
            echo "Unknown option: $1" >&2
            usage
            ;;
        *)
            break
            ;;
    esac
done

KEY_FILE="${KEY_DIR}/signing-key.pem"
CERT_FILE="${KEY_DIR}/signing-cert.pem"

cmd_generate() {
    if [[ -f "$KEY_FILE" && -f "$CERT_FILE" ]]; then
        echo "Keys already exist in ${KEY_DIR}" >&2
        return 0
    fi

    mkdir -p "$KEY_DIR"

    # Generate RSA-2048 private key
    openssl genrsa -out "$KEY_FILE" 2048 2>/dev/null

    # Create self-signed certificate (1 year, code-signing EKU)
    openssl req -new -x509 \
        -key "$KEY_FILE" \
        -out "$CERT_FILE" \
        -days 365 \
        -subj "/CN=local-signing-tool/O=api-server-proxy" \
        -addext "keyUsage=digitalSignature" \
        -addext "extendedKeyUsage=codeSigning" \
        2>/dev/null

    echo "Generated signing key and certificate in ${KEY_DIR}" >&2
    echo "  Private key:  ${KEY_FILE}" >&2
    echo "  Certificate:  ${CERT_FILE}" >&2
}

cmd_sign() {
    if [[ $# -lt 1 ]]; then
        echo "Usage: $0 sign <base64-payload>" >&2
        exit 1
    fi

    local payload_b64="$1"

    if [[ ! -f "$KEY_FILE" ]]; then
        echo "ERROR: Signing key not found at ${KEY_FILE}. Run 'generate' first." >&2
        exit 1
    fi

    # Decode base64 payload → SHA-256 hash → RSA-PSS sign → base64 output.
    # openssl pkeyutl with -pkeyopt rsa_padding_mode:pss matches the Go
    # rsa.SignPSS(SHA256, PSSSaltLengthEqualsHash) behaviour.
    printf '%s' "$payload_b64" | base64 -d \
        | openssl dgst -sha256 -binary \
        | openssl pkeyutl -sign \
            -inkey "$KEY_FILE" \
            -pkeyopt rsa_padding_mode:pss \
            -pkeyopt rsa_pss_saltlen:32 \
            -pkeyopt digest:sha256 \
        | base64 -w0

    echo  # trailing newline
}

cmd_cert() {
    if [[ ! -f "$CERT_FILE" ]]; then
        echo "ERROR: Certificate not found at ${CERT_FILE}. Run 'generate' first." >&2
        exit 1
    fi
    echo "$CERT_FILE"
}

# Dispatch
case "${COMMAND:-}" in
    generate)
        cmd_generate
        ;;
    sign)
        cmd_sign "$@"
        ;;
    cert)
        cmd_cert
        ;;
    *)
        usage
        ;;
esac
