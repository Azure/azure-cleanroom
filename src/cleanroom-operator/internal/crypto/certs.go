// Package crypto provides certificate and key generation for CCF
// consortium members using the same algorithms as keygenerator.sh.
package crypto

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"fmt"
	"math/big"
	"time"
)

const (
	rsaKeyBits     = 2048
	certValidYears = 2
)

// MemberCerts holds the generated identity certificate and key,
// plus optional encryption key pair.
type MemberCerts struct {
	// CertPEM is the PEM-encoded X.509 identity certificate.
	CertPEM []byte
	// PrivateKeyPEM is the PEM-encoded EC private key.
	PrivateKeyPEM []byte
	// EncryptionPublicKeyPEM is the PEM-encoded RSA public key
	// (nil if not requested).
	EncryptionPublicKeyPEM []byte
	// EncryptionPrivateKeyPEM is the PEM-encoded RSA private key
	// (nil if not requested).
	EncryptionPrivateKeyPEM []byte
}

// GenerateMemberCerts generates an ECDSA identity certificate
// (secp384r1 / P-384) and optionally an RSA-2048 encryption key
// pair. This mirrors the output of CCF's keygenerator.sh.
func GenerateMemberCerts(
	name string,
	generateEncryptionKey bool,
) (*MemberCerts, error) {
	// Generate EC P-384 private key for identity.
	privKey, err := ecdsa.GenerateKey(
		elliptic.P384(), rand.Reader,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"generating EC private key: %w", err,
		)
	}

	// Create self-signed certificate.
	serial, err := rand.Int(
		rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128),
	)
	if err != nil {
		return nil, fmt.Errorf(
			"generating serial number: %w", err,
		)
	}

	now := time.Now()
	template := &x509.Certificate{
		SerialNumber: serial,
		Subject: pkix.Name{
			CommonName: name,
		},
		NotBefore:             now,
		NotAfter:              now.AddDate(certValidYears, 0, 0),
		KeyUsage:              x509.KeyUsageDigitalSignature,
		BasicConstraintsValid: true,
	}

	certDER, err := x509.CreateCertificate(
		rand.Reader, template, template, &privKey.PublicKey,
		privKey,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating certificate: %w", err,
		)
	}

	certPEM := pem.EncodeToMemory(&pem.Block{
		Type:  "CERTIFICATE",
		Bytes: certDER,
	})

	privKeyDER, err := x509.MarshalECPrivateKey(privKey)
	if err != nil {
		return nil, fmt.Errorf(
			"marshaling EC private key: %w", err,
		)
	}
	privKeyPEM := pem.EncodeToMemory(&pem.Block{
		Type:  "EC PRIVATE KEY",
		Bytes: privKeyDER,
	})

	result := &MemberCerts{
		CertPEM:       certPEM,
		PrivateKeyPEM: privKeyPEM,
	}

	// Generate RSA encryption key pair if requested.
	if generateEncryptionKey {
		rsaKey, err := rsa.GenerateKey(rand.Reader, rsaKeyBits)
		if err != nil {
			return nil, fmt.Errorf(
				"generating RSA encryption key: %w", err,
			)
		}

		rsaPubDER, err := x509.MarshalPKIXPublicKey(
			&rsaKey.PublicKey,
		)
		if err != nil {
			return nil, fmt.Errorf(
				"marshaling RSA public key: %w", err,
			)
		}

		result.EncryptionPublicKeyPEM = pem.EncodeToMemory(
			&pem.Block{
				Type:  "PUBLIC KEY",
				Bytes: rsaPubDER,
			},
		)
		result.EncryptionPrivateKeyPEM = pem.EncodeToMemory(
			&pem.Block{
				Type:  "RSA PRIVATE KEY",
				Bytes: x509.MarshalPKCS1PrivateKey(rsaKey),
			},
		)
	}

	return result, nil
}
