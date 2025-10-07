# Note
The various trusted root CA certs are taken from https://learn.microsoft.com/en-us/azure/security/fundamentals/azure-CA-details?tabs=root-and-subordinate-cas-list#certificate-authority-details
and concatenated together in PEM format to create the set_ca_cert_bundle proposal.

Running `gen_cert_bundle.ps1` downloads all the certificates and creates a single line string that is the concatenation of the PEM format version of each of the certs. The single line string
is saved in `certs/cert_bundle.pem` and the string can be used as the `args.cert_bundle` value for the `set_ca_cert_bundle` proposal.

