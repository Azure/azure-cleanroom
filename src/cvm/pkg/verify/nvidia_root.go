package verify

import (
	"crypto/x509"
	"encoding/pem"
	"fmt"
)

// Well-known NVIDIA GPU Attestation Root CA certificates.
// These are used to anchor the GPU attestation certificate chain.
// The GPU leaf certificate (which signs the SPDM attestation report)
// must chain back to one of these roots.
//
// This mirrors the AMD ARK root trust approach used in ark.go.
//
// Source: https://github.com/NVIDIA/nvtrust/blob/main/guest_tools/gpu_verifiers/local_gpu_verifier/src/verifier/certs/

// nvidiaDeviceIdentityRootPEM is the ECDSA-P384 certificate of the
// "NVIDIA Device Identity CA" — the root of trust for all NVIDIA GPU
// attestation certificate chains. This single root covers all GPU
// architectures (Hopper, Blackwell, etc.).
//
// Downloaded from: nvtrust/guest_tools/gpu_verifiers/local_gpu_verifier/src/verifier/certs/verifier_device_root.pem
const nvidiaDeviceIdentityRootPEM = `-----BEGIN CERTIFICATE-----
MIICCzCCAZCgAwIBAgIQLTZwscoQBBHB/sDoKgZbVDAKBggqhkjOPQQDAzA1MSIw
IAYDVQQDDBlOVklESUEgRGV2aWNlIElkZW50aXR5IENBMQ8wDQYDVQQKDAZOVklE
SUEwIBcNMjExMTA1MDAwMDAwWhgPOTk5OTEyMzEyMzU5NTlaMDUxIjAgBgNVBAMM
GU5WSURJQSBEZXZpY2UgSWRlbnRpdHkgQ0ExDzANBgNVBAoMBk5WSURJQTB2MBAG
ByqGSM49AgEGBSuBBAAiA2IABA5MFKM7+KViZljbQSlgfky/RRnEQScW9NDZF8SX
gAW96r6u/Ve8ZggtcYpPi2BS4VFu6KfEIrhN6FcHG7WP05W+oM+hxj7nyA1r1jkB
2Ry70YfThX3Ba1zOryOP+MJ9vaNjMGEwDwYDVR0TAQH/BAUwAwEB/zAOBgNVHQ8B
Af8EBAMCAQYwHQYDVR0OBBYEFFeF/4PyY8xlfWi3Olv0jUrL+0lfMB8GA1UdIwQY
MBaAFFeF/4PyY8xlfWi3Olv0jUrL+0lfMAoGCCqGSM49BAMDA2kAMGYCMQCPeFM3
TASsKQVaT+8S0sO9u97PVGCpE9d/I42IT7k3UUOLSR/qvJynVOD1vQKVXf0CMQC+
EY55WYoDBvs2wPAH1Gw4LbcwUN8QCff8bFmV4ZxjCRr4WXTLFHBKjbfneGSBWwA=
-----END CERTIFICATE-----`

// nvidiaDeviceIdentityRootPublicKeyDER is the PKIX DER-encoded public key
// of the NVIDIA root CA, computed once at init for fast comparison.
var nvidiaDeviceIdentityRootPublicKeyDER []byte

func init() {
	block, _ := pem.Decode([]byte(nvidiaDeviceIdentityRootPEM))
	if block == nil {
		panic("failed to decode NVIDIA Device Identity CA PEM")
	}
	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		panic(fmt.Sprintf("failed to parse NVIDIA Device Identity CA certificate: %v", err))
	}

	der, err := x509.MarshalPKIXPublicKey(cert.PublicKey)
	if err != nil {
		panic(fmt.Sprintf("failed to marshal NVIDIA root public key: %v", err))
	}
	nvidiaDeviceIdentityRootPublicKeyDER = der
}
