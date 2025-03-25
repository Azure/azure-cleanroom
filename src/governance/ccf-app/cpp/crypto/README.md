# Note
The self-signed and endorsed certificate generation code here is taken from https://github.com/microsoft/CCF/tree/main/src/crypto
and compiled into the cpp extension which then exposes the cert generation methods to the clean room JS application.

The certificate generation code in CCF is internal to its needs for creating service and node certs 
so the same is not exposed in the ccf.crypto JS package for consumption by the CCF application. 
Hence we have a copy of that code here so that we can adapt it for our needs and not be affected by 
CCF changes around their evolving requirements for cert generation.