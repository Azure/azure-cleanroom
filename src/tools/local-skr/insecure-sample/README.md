# Steps to refresh local-skr with new maa-request.json
- Run `samples/reports/generate-reports.ps1` to update the public/private key pair and MAA attestation request that is used in the `local-skr` container.
- Build the new `local-skr` image which will now contain the new request json and its corresponding public/private key pair.
