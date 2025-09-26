# Local IDP implementation used for testing CCF JWT authentication.
import base64
import datetime
import hashlib
import time
import uuid
from typing import Tuple

import jwt
from cryptography import x509
from cryptography.hazmat.backends import default_backend
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec, ed25519, padding, rsa, x25519
from cryptography.hazmat.primitives.serialization import (
    Encoding,
    NoEncryption,
    PrivateFormat,
    PublicFormat,
    load_pem_private_key,
    load_pem_public_key,
)
from cryptography.x509 import load_der_x509_certificate, load_pem_x509_certificate
from cryptography.x509.oid import NameOID
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

RECOMMENDED_RSA_PUBLIC_EXPONENT = 65537

app = FastAPI(description="Local IDP API", debug=True)
app.signing_pub_pem = None
app.signing_priv_pem = None
app.signing_cert = None
app.signing_kid = uuid.UUID(int=0)
app.issuer_url = "http://localhost:8321/oidc"


@app.get("/ready")
async def root():
    return {"status": "up"}


@app.post("/generatesigningkey")
async def generateSigingKey():
    app.signing_priv_pem, app.signing_pub_pem = generate_rsa_keypair(2048)
    app.signing_cert = generate_cert(app.signing_priv_pem, cn="local-idp")
    app.signing_x5c = base64.b64encode(cert_pem_to_der(app.signing_cert)).decode(
        "ascii"
    )
    app.signing_kid = uuid.uuid4()
    return {"kid": app.signing_kid.hex, "pem": app.signing_cert, "x5c": app.signing_x5c}


@app.get("/getsigningkey")
async def getSigningKey():
    if app.signing_pub_pem is None:
        raise HTTPException(
            status_code=400,
            detail={
                "code": "NoSigningKeyGenerated",
                "message": "No signing key generated",
            },
        )

    return {"kid": app.signing_kid.hex, "pem": app.signing_cert, "x5c": app.signing_x5c}


class IssuerUrl(BaseModel):
    url: str


@app.post("/setissuerurl")
async def setIssuerUrl(issuerUrl: IssuerUrl):
    # validate the url
    if not issuerUrl.url.startswith("http://") and not issuerUrl.url.startswith(
        "https://"
    ):
        raise HTTPException(
            status_code=400,
            detail={
                "code": "InvalidIssuerUrl",
                "message": "Invalid issuer url",
            },
        )
    app.issuer_url = issuerUrl.url
    return {"url": app.issuer_url}


@app.get("/oidc/.well-known/openid-configuration")
async def getOpenIdConfig():
    # return the openid configuration
    if app.signing_pub_pem is None:
        raise HTTPException(
            status_code=400,
            detail={
                "code": "NoSigningKeyGenerated",
                "message": "No signing key generated",
            },
        )
    return {
        "issuer": app.issuer_url,
        "jwks_uri": app.issuer_url + "/keys",
        "response_types_supported": ["id_token"],
        "id_token_signing_alg_values_supported": ["RS256"],
    }


@app.get("/oidc/keys")
async def getJwks():
    # return signing_pub_pem as a jwks
    if app.signing_pub_pem is None:
        raise HTTPException(
            status_code=400,
            detail={
                "code": "NoSigningKeyGenerated",
                "message": "No signing key generated",
            },
        )

    pub_key = load_pem_public_key(
        app.signing_pub_pem.encode("ascii"), backend=default_backend()
    )
    pub_key_jwks = pub_key.public_bytes(Encoding.DER, PublicFormat.SubjectPublicKeyInfo)
    return {
        "keys": [
            {
                "kty": "RSA",
                "alg": "RS256",
                "use": "sig",
                "kid": app.signing_kid.hex,
                "n": pub_key_jwks.hex(),
                "e": str(RECOMMENDED_RSA_PUBLIC_EXPONENT),
            }
        ]
    }


@app.post("/oauth/token")
async def getToken(
    nbf: str | None = None,
    exp: str | None = None,
    sub: str | None = None,
    tid: str | None = None,
    aud: str | None = None,
    oid: str | None = None,
    iss: str | None = None,
):
    # return a token
    if app.signing_pub_pem is None:
        raise HTTPException(
            status_code=400,
            detail={
                "code": "NoSigningKeyGenerated",
                "message": "No signing key generated",
            },
        )

    claims = {}
    now = int(time.time())
    nbf = nbf if nbf is not None else str(now - 10)
    claims["nbf"] = int(nbf)
    exp = exp if exp is not None else str(now + 3600)  # 1 hour
    claims["exp"] = int(exp)
    iss = iss if iss is not None else app.issuer_url
    claims["iss"] = iss
    if sub is not None:
        claims["sub"] = sub
    if tid is not None:
        claims["tid"] = tid
    if aud is not None:
        claims["aud"] = aud
    if oid is not None:
        claims["oid"] = oid

    token = jwt.encode(
        claims,
        app.signing_priv_pem,
        algorithm="RS256",
        headers={"kid": app.signing_kid.hex},
    )

    return {"accessToken": token}


def generate_rsa_keypair(key_size: int) -> Tuple[str, str]:
    assert key_size >= 2048
    priv = rsa.generate_private_key(
        public_exponent=RECOMMENDED_RSA_PUBLIC_EXPONENT,
        key_size=key_size,
        backend=default_backend(),
    )
    pub = priv.public_key()
    priv_pem = priv.private_bytes(
        Encoding.PEM, PrivateFormat.PKCS8, NoEncryption()
    ).decode("ascii")
    pub_pem = pub.public_bytes(Encoding.PEM, PublicFormat.SubjectPublicKeyInfo).decode(
        "ascii"
    )
    return priv_pem, pub_pem


def generate_cert(
    priv_key_pem: str,
    cn=None,
    issuer_priv_key_pem=None,
    issuer_cn=None,
    ca=False,
    valid_from=None,
    validity_days=10,
) -> str:
    cn = cn or "dummy"
    if issuer_priv_key_pem is None:
        issuer_priv_key_pem = priv_key_pem
    if issuer_cn is None:
        issuer_cn = cn
    if valid_from is None:
        valid_from = datetime.datetime.utcnow()
    priv = load_pem_private_key(priv_key_pem.encode("ascii"), None, default_backend())
    pub = priv.public_key()
    issuer_priv = load_pem_private_key(
        issuer_priv_key_pem.encode("ascii"), None, default_backend()
    )
    subject = x509.Name(
        [
            x509.NameAttribute(
                NameOID.COMMON_NAME, hashlib.sha256(cn.encode("ascii")).hexdigest()
            ),
        ]
    )
    issuer = x509.Name(
        [
            x509.NameAttribute(
                NameOID.COMMON_NAME,
                hashlib.sha256(issuer_cn.encode("ascii")).hexdigest(),
            ),
        ]
    )
    builder = (
        x509.CertificateBuilder()
        .subject_name(subject)
        .issuer_name(issuer)
        .public_key(pub)
        .serial_number(x509.random_serial_number())
        .not_valid_before(valid_from)
        .not_valid_after(valid_from + datetime.timedelta(days=validity_days))
    )
    if ca:
        builder = builder.add_extension(
            x509.BasicConstraints(ca=True, path_length=None),
            critical=True,
        )

    cert = builder.sign(issuer_priv, hashes.SHA256(), default_backend())

    return cert.public_bytes(Encoding.PEM).decode("ascii")


def cert_pem_to_der(pem: str) -> bytes:
    cert = load_pem_x509_certificate(pem.encode("ascii"), default_backend())
    return cert.public_bytes(Encoding.DER)
