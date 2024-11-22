// Script responsible for converting the RSA private key retrieved from Key Vault Managed HSM via
// the SKR sidecar.
// The SKR sidecar retrieves the key in JWK format. This script converts into PEM format.
//
// Sample invocation: node jwk-to-pem.js ./keyrelease.out > privkey.pem
// where "./keyrelease.out" is the output of SKR sidecar's '/key/release' API endpoint.

const fs = require("fs");

var args = process.argv;
// console.log(args);
let keyreleaseoutfile = args[2];
let rawdata = fs.readFileSync(keyreleaseoutfile);
let keyrelease = JSON.parse(rawdata);
var jwk = JSON.parse(keyrelease.key);

var jwkToPem = require("jwk-to-pem");
var options = { private: true };
var pem = jwkToPem(jwk, options);
console.log(pem);
