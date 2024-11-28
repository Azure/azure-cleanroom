#!/bin/bash

gen_members_config() {

    # Get the directory of this script
    TOOLS_DIR="$(dirname "$(realpath "${BASH_SOURCE[0]}")")"
    source $TOOLS_DIR/resolve_ccf_workspace.sh && resolve_ccf_workspace

    cat <<EOF > "$CCF_WORKSPACE/members_config.json"
[
  {
    "certificate": "$CCF_WORKSPACE/operator_cert.pem",
    "encryptionPublicKey": "$CCF_WORKSPACE/operator_enc_pubk.pem",
    "memberData": {
      "identifier": "operator",
      "is_operator": true
    }
  }
]
EOF
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  gen_members_config
  echo "Members config saved at: $CCF_WORKSPACE/members_config.json"
fi
