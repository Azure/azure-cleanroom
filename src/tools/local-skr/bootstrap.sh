#! /bin/bash

if [ -e "/dev/sev" ] || [ -e "/dev/sev-guest" ]; then
  # This tool is not meant to run in a SEV-SNP environment. Fail its execution.
  echo "Running in SEV-SNP environment. Running this tool in such an environment is not supported. Exiting."
  exit 1
else
  echo "Running in insecure virtual mode. Starting container."
  export ccrgovPrivKey="./ccr_gov_priv_key.pem"
  export ccrgovPubKey="./ccr_gov_pub_key.pem"
  export maaRequest="./maa-request.json"
  # Use exec so that SIGTERM is propagated to the child process and the process can be gracefully stopped.
  exec dotnet ./local-skr.dll
fi

