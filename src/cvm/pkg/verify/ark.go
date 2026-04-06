package verify

// Well-known AMD Attestation Root Key (ARK) public keys.
// These are the RSA-4096 public keys from AMD's self-signed ARK root
// certificates. They are used to establish trust in the platform
// certificate chain provided by THIM: the public key of the ARK in
// the provided chain must match one of these well-known keys.
//
// Source: extracted from https://kdsintf.amd.com/vcek/v1/{product}/cert_chain
// Downloaded: 2026-02-22
//
// This matches the approach used by the C# SnpReport.AmdRootPublicKey.

// arkMilanPublicKeyPEM is the RSA-4096 public key of the AMD ARK for Milan
// (EPYC 7003).
const arkMilanPublicKeyPEM = `-----BEGIN PUBLIC KEY-----
MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA0Ld52RJOdeiJlqK2JdsV
mD7FktuotWwX1fNgW41XY9Xz1HEhSUmhLz9Cu9DHRlvgJSNxbeYYsnJfvyjx1MfU
0V5tkKiU1EesNFta1kTA0szNisdYc9isqk7mXT5+KfGRbfc4V/9zRIcE8jlHN61S
1ju8X93+6dxDUrG2SzxqJ4BhqyYmUDruPXJSX4vUc01P7j98MpqOS95rORdGHeI5
2Naz5m2B+O+vjsC060d37jY9LFeuOP4Meri8qgfi2S5kKqg/aF6aPtuAZQVR7u3K
FYXP59XmJgtcog05gmI0T/OitLhuzVvpZcLph0odh/1IPXqx3+MnjD97A7fXpqGd
/y8KxX7jksTEzAOgbKAeam3lm+3yKIcTYMlsRMXPcjNbIvmsBykD//xSniusuHBk
gnlENEWx1UcbQQrs+gVDkuVPhsnzIRNgYvM48Y+7LGiJYnrmE8xcrexekBxrva2V
9TJQqnN3Q53kt5viQi3+gCfmkwC0F0tirIZbLkXPrPwzZ0M9eNxhIySb2npJfgnq
z55I0u33wh4r0ZNQeTGfw03MBUtyuzGesGkcw+loqMaq1qR4tjGbPYxCvpCq7+Og
pCCoMNit2uLo9M18fHz10lOMT8nWAUvRZFzteXCm+7PHdYPlmQwUw3LvenJ/ILXo
QPHfbkH0CyPfhl1jWhJFZasCAwEAAQ==
-----END PUBLIC KEY-----`

// arkGenoaPublicKeyPEM is the RSA-4096 public key of the AMD ARK for Genoa
// (EPYC 9004).
const arkGenoaPublicKeyPEM = `-----BEGIN PUBLIC KEY-----
MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA3Cd95S/uFOuRIskW9vz9
VDBF69NDQF79oRhL/L2PVQGhK3YdfEBgpF/JiwWFBsT/fXDhzA01p3LkcT/7Ldjc
RfKXjHl+0Qq/M4dZkh6QDoUeKzNBLDcBKDDGWo3v35NyrxbA1DnkYwUKU5AAk4P9
4tKXLp80oxt84ahyHoLmc/LqsGsp+oq1Bz4PPsYLwTG4iMKVaaT90/oZ4I8oibSr
u92vJhlqWO27d/Rxc3iUMyhNeGToOvgx/iUo4gGpG61NDpkEUvIzuKcaMx8IdTpW
g2DF6SwF0IgVMffnvtJmA68BwJNWo1E4PLJdaPfBifcJpuBFwNVQIPQEVX3aP89H
JSp8YbY9lySS6PlVEqTBBtaQmi4ATGmMR+n2K/e+JAhU2Gj7jIpJhOkdH9firQDn
mlA2SFfJ/Cc0mGNzW9RmIhyOUnNFoclmkRhl3/AQU5Ys9Qsan1jT/EiyT+pCpmnA
+y9edvhDCbOG8F2oxHGRdTBkylungrkXJGYiwGrR8kaiqv7NN8QhOBMqYjcbrkEr
0f8QMKklIS5ruOfqlLMCBw8JLB3LkjpWgtD7OpxkzSsohN47Uom86RY6lp72g8eX
HP1qYrnvhzaG1S70vw6OkbaaC9EjiH/uHgAJQGxon7u0Q7xgoREWA/e7JcBQwLg8
0Hq/sbRuqesxz7wBWSY254cCAwEAAQ==
-----END PUBLIC KEY-----`

// arkTurinPublicKeyPEM is the RSA-4096 public key of the AMD ARK for Turin
// (EPYC 9005).
const arkTurinPublicKeyPEM = `-----BEGIN PUBLIC KEY-----
MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAwaAriB7EIuVc4ZB1wD3Y
fDx+9eyS7+izm0Jj3W772NINCWl8Bj3w/JD2ZjmbRxWdIq/4d9iarCKorXloJUB
1jRdgxqccTx1aOoig42w1XhVVJT7K457wT5ZLNJgQaxqa9Etkwjd6+9sOhlCDE9
l43kQ0R2BikVJa/uyyVOSwEk5w5tXKOuGjvq6QtAMJasW38wlqRDaKEGtZ9VUgG
on27ZuL4sTJuC/azz9/iQBw8kEilzOl95AiTkeY5jSEBDWbAnZk5qlM7kISKG20
kgQm14mhNKDI2p2oua+zuAG7i52epoRF2GfU0TYk/yf+vCNB2tnechFQuP2e8bL
95ZdqPi9/UWw4JXjtdEA4u2JYplSSUPQVAXKt6LVqujtJcM59JKr2u0XQ75KwxcM
p15gSXhBfInvPwuAY4dEwwGqT8oIg4esPHwEsmChhYeDIxPG9R4fx9O0q6p8Gb+
HXlTiS47P9YNeOpidOUKzDl/S1OvhDtSL8LJc24QATFydo/iD/KUdvFTRlD0crk
AMkZLoWQ8hLDGc6BZJXsdd7Zf2e4UW3tI/1oh/2t23O3zyhTcv5gDbABu0LjVe9
8uRnS15SMwK//lJt9e5BqKvgABkSoABf+B4VFtPVEX0ygrYaFaI9i5ABrxnVBmzX
pRb21iI1NlNCfOGUPIhVpWECAwEAAQ==
-----END PUBLIC KEY-----`

// wellKnownARKPublicKeys maps AMD product names to their well-known ARK
// public key PEM strings.
var wellKnownARKPublicKeys = map[string]string{
	"Milan": arkMilanPublicKeyPEM,
	"Genoa": arkGenoaPublicKeyPEM,
	"Turin": arkTurinPublicKeyPEM,
}
