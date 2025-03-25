// CGS specific actions below.
const accepted_contracts_table = "public:ccf.gov.accepted_contracts";
actions.set(
  "set_contract",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");
      checkType(args.contract, "object", "contract");
    },
    function (args, proposalId) {
      let contract_item = {};
      contract_item.data = args.contract.data;

      // Save the final votes tally along with the accepted contract for reporting purposes.
      let proposals = ccf.kv["public:ccf.gov.proposals_info"];
      let proposalInfoBuffer = proposals.get(ccf.strToBuf(proposalId));
      if (proposalInfoBuffer === undefined) {
        throw new Error(`Can't find proposal info for ${proposalId}`);
      }

      const proposalInfo = ccf.bufToJsonCompatible(proposalInfoBuffer);
      contract_item.proposalId = proposalId;
      contract_item.finalVotes = proposalInfo.final_votes;

      ccf.kv[accepted_contracts_table].set(
        ccf.strToBuf(args.contractId),
        ccf.jsonCompatibleToBuf(contract_item)
      );
    }
  )
);

actions.set(
  "set_deployment_spec",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");
      checkType(args.spec, "object", "spec");

      if (
        !ccf.kv[accepted_contracts_table].has(ccf.strToBuf(args.contractId))
      ) {
        throw new Error(
          `Deployment spec can only be proposed once contract '${args.contractId}' has been accepted.`
        );
      }
    },
    function (args) {
      const deployment_specs_table = "public:ccf.gov.deployment_specs";
      let deployment_spec_item = {};
      deployment_spec_item.data = args.spec.data;
      ccf.kv[deployment_specs_table].set(
        ccf.strToBuf(args.contractId),
        ccf.jsonCompatibleToBuf(deployment_spec_item)
      );
    }
  )
);

const oidc_issuer_table = "public:ccf.gov.oidc_issuer";
const idp_oidc_config_IssuerUrl = "issuerUrl";
const idp_oidc_config_enabled = "enabled";
const idp_oidc_config_requestId = "generateSigningKeyRequestId";
const idp_oidc_config_kid = "generateSigningKeyKid";

actions.set(
  "enable_oidc_issuer",
  new Action(
    function (args) {
      checkType(args.kid, "string", "kid");
    },
    function (args) {
      if (
        !ccf.kv[oidc_issuer_table].has(ccf.strToBuf(idp_oidc_config_enabled))
      ) {
        ccf.kv[oidc_issuer_table].set(
          ccf.strToBuf(idp_oidc_config_enabled),
          ccf.strToBuf("true")
        );
        ccf.kv[oidc_issuer_table].set(
          ccf.strToBuf(idp_oidc_config_requestId),
          ccf.strToBuf("1")
        );
        ccf.kv[oidc_issuer_table].set(
          ccf.strToBuf(idp_oidc_config_kid),
          ccf.strToBuf(args.kid)
        );
      }
    }
  )
);

actions.set(
  "set_oidc_issuer_url",
  new Action(
    function (args) {
      checkType(args.issuer_url, "string", "issuer_url");
    },
    function (args) {
      ccf.kv[oidc_issuer_table].set(
        ccf.strToBuf("issuerUrl"),
        ccf.strToBuf(args.issuer_url)
      );
    }
  )
);

actions.set(
  "oidc_issuer_enable_rotate_signing_key",
  new Action(
    function (args) {},
    function (args) {
      if (
        ccf.kv[oidc_issuer_table].has(ccf.strToBuf(idp_oidc_config_requestId))
      ) {
        let reqId = Number(
          ccf.bufToStr(
            ccf.kv[oidc_issuer_table].get(
              ccf.strToBuf(idp_oidc_config_requestId)
            )
          )
        );
        reqId++;
        ccf.kv[oidc_issuer_table].set(
          ccf.strToBuf(idp_oidc_config_requestId),
          ccf.strToBuf(reqId.toString())
        );
      }
    }
  )
);

const CLAIMS = {
  "x-ms-attestation-type": "string",
  "x-ms-compliance-status": "string",
  "x-ms-policy-hash": "string",
  "vm-configuration-secure-boot": "boolean",
  "vm-configuration-secure-boot-template-id": "string",
  "vm-configuration-tpm-enabled": "boolean",
  "vm-configuration-vmUniqueId": "string",
  "x-ms-sevsnpvm-authorkeydigest": "string",
  "x-ms-sevsnpvm-bootloader-svn": "number",
  "x-ms-sevsnpvm-familyId": "string",
  "x-ms-sevsnpvm-guestsvn": "number",
  "x-ms-sevsnpvm-hostdata": "string",
  "x-ms-sevsnpvm-idkeydigest": "string",
  "x-ms-sevsnpvm-imageId": "string",
  "x-ms-sevsnpvm-is-debuggable": "boolean",
  "x-ms-sevsnpvm-launchmeasurement": "string",
  "x-ms-sevsnpvm-microcode-svn": "number",
  "x-ms-sevsnpvm-migration-allowed": "boolean",
  "x-ms-sevsnpvm-reportdata": "string",
  "x-ms-sevsnpvm-reportid": "string",
  "x-ms-sevsnpvm-smt-allowed": "boolean",
  "x-ms-sevsnpvm-snpfw-svn": "number",
  "x-ms-sevsnpvm-tee-svn": "number",
  "x-ms-sevsnpvm-vmpl": "number",
  "x-ms-ver": "string"
};

actions.set(
  "set_clean_room_policy",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");
      checkType(args.type, "string", "type");
      checkType(args.claims, "object", "claims");

      if (args.type !== "add" && args.type !== "remove") {
        throw new Error(
          `Clean Room Policy with type '${type}' is not supported`
        );
      }

      Object.keys(args.claims).forEach((key) => {
        if (CLAIMS[key] === undefined) {
          throw new Error(`The claim '${key}' is not an allowed claim`);
        }
      });

      if (
        !ccf.kv[accepted_contracts_table].has(ccf.strToBuf(args.contractId))
      ) {
        throw new Error(
          `Clean Room Policy can only be proposed once contract '${args.contractId}' has been accepted.`
        );
      }
    },
    function (args) {
      const cleanRoomPolicyMapName =
        "public:ccf.gov.policies.cleanroom-" + args.contractId;
      // Function to add  policy claims
      const add = (claims) => {
        let items = [];
        console.log(
          `Add claims to clean room policy: ${JSON.stringify(claims)}`
        );
        Object.keys(claims).forEach((key) => {
          let item = claims[key];
          // Make sure item is always an array
          if (!Array.isArray(item)) {
            item = [item];
          }

          let keyBuf = ccf.strToBuf(key);
          if (ccf.kv[cleanRoomPolicyMapName].has(keyBuf)) {
            // Key is already available
            const itemsBuf = ccf.kv[cleanRoomPolicyMapName].get(keyBuf);
            items = ccf.bufToStr(itemsBuf);
            console.log(`key: ${key} already exist: ${items}`);
            items = JSON.parse(items);
            if (typeof item[0] === "boolean") {
              //booleans are single value arrays
              items = item;
            } else {
              // loop through the input and add it to the existing set
              item.forEach((i) => {
                items.push(i);
              });
            }
          } else {
            // set single value
            items = item;
          }

          // prepare and store items
          let jsonItems = JSON.stringify(items);
          let jsonItemsBuf = ccf.strToBuf(jsonItems);
          console.log(
            `Voted clean room policy item. Key: ${key}, value: ${jsonItems}`
          );
          ccf.kv[cleanRoomPolicyMapName].set(keyBuf, jsonItemsBuf);
        });
      };

      // Function to remove clean room policy claims
      const remove = (claims) => {
        let items = [];
        console.log(
          `Remove claims to clean room policy: ${JSON.stringify(claims)}`
        );
        Object.keys(claims).forEach((key) => {
          let item = claims[key];
          // Make sure item is always an array
          if (!Array.isArray(item)) {
            item = [item];
          }

          let keyBuf = ccf.strToBuf(key);
          if (ccf.kv[cleanRoomPolicyMapName].has(keyBuf)) {
            // Key must be available
            const itemsBuf = ccf.kv[cleanRoomPolicyMapName].get(keyBuf);
            items = ccf.bufToStr(itemsBuf);
            console.log(`key: ${key} exist: ${items}`);
            items = JSON.parse(items);
            if (typeof item[0] === "boolean") {
              //booleans are single value arrays, removing will remove the whole key
              ccf.kv[cleanRoomPolicyMapName].delete(keyBuf);
            } else {
              // loop through the input and delete it from the existing set
              item.forEach((i) => {
                if (items.filter((ii) => ii === i).length === 0) {
                  throw new Error(
                    `Trying to remove value '${i}' from ${items} and it does not exist`
                  );
                }
                // Remove value from list
                const index = items.indexOf(i);
                if (index > -1) {
                  items.splice(index, 1);
                }
              });
              // update items
              if (items.length === 0) {
                ccf.kv[cleanRoomPolicyMapName].delete(keyBuf);
              } else {
                let jsonItems = JSON.stringify(items);
                let jsonItemsBuf = ccf.strToBuf(jsonItems);
                ccf.kv[cleanRoomPolicyMapName].set(keyBuf, jsonItemsBuf);
              }
            }
          } else {
            throw new Error(
              `Cannot remove values of ${key} because the key does not exist in the clean room policy claims`
            );
          }
        });
      };

      const type = args.type;
      switch (type) {
        case "add":
          add(args.claims);
          break;
        case "remove":
          remove(args.claims);
          break;
        default:
          throw new Error(
            `Clean Room Policy with type '${type}' is not supported`
          );
      }
    }
  )
);

const ca_table = "public:ccf.gov.ca";

actions.set(
  "enable_ca",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");
      if (
        !ccf.kv[accepted_contracts_table].has(ccf.strToBuf(args.contractId))
      ) {
        throw new Error(
          `Enabling the CA can only be proposed once contract '${args.contractId}' has been accepted.`
        );
      }
    },
    function (args) {
      const rawConfig = ccf.kv[ca_table].get(ccf.strToBuf(args.contractId));
      const caConfig =
        rawConfig === undefined ? {} : ccf.bufToJsonCompatible(rawConfig);
      if (caConfig.enabled !== "true") {
        caConfig.enabled = "true";
        caConfig.requestId = "1";
        ccf.kv[ca_table].set(
          ccf.strToBuf(args.contractId),
          ccf.jsonCompatibleToBuf(caConfig)
        );
      }
    }
  )
);

actions.set(
  "ca_enable_rotate_signing_key",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");
      if (
        !ccf.kv[accepted_contracts_table].has(ccf.strToBuf(args.contractId))
      ) {
        throw new Error(
          `Enabling the CA can only be proposed once contract '${args.contractId}' has been accepted.`
        );
      }

      if (!ccf.kv[ca_table].has(ccf.strToBuf(args.contractId))) {
        throw new Error(
          `Cannot rotate the CA signing key as CA is not enabled for the contract.`
        );
      }

      const rawConfig = ccf.kv[ca_table].get(ccf.strToBuf(args.contractId));
      const caConfig = ccf.bufToJsonCompatible(rawConfig);
      if (caConfig.enabled !== "true") {
        throw new Error(
          `Cannot rotate the CA signing key as CA status is '${caConfig.enabled}' for the contract.`
        );
      }
    },
    function (args) {
      const rawConfig = ccf.kv[ca_table].get(ccf.strToBuf(args.contractId));
      const caConfig = ccf.bufToJsonCompatible(rawConfig);
      let reqId = Number(caConfig.requestId);
      reqId++;
      caConfig.requestId = reqId.toString();
      ccf.kv[ca_table].set(
        ccf.strToBuf(args.contractId),
        ccf.jsonCompatibleToBuf(caConfig)
      );
    }
  )
);
const telemetryStatusTable = "public:ccf.gov.contracts_telemetry_status";
const loggingStatusTable = "public:ccf.gov.contracts_logging_status";

actions.set(
  "set_contract_runtime_options_enable_telemetry",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");

      if (
        !ccf.kv[accepted_contracts_table].has(ccf.strToBuf(args.contractId))
      ) {
        throw new Error(
          `Action can only be proposed once contract '${args.contractId}' has been accepted.`
        );
      }
    },
    function (args) {
      let item = {};
      item.status = "enabled";
      ccf.kv[telemetryStatusTable].set(
        ccf.strToBuf(args.contractId),
        ccf.jsonCompatibleToBuf(item)
      );
    }
  )
);
actions.set(
  "set_contract_runtime_options_disable_telemetry",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");

      if (
        !ccf.kv[accepted_contracts_table].has(ccf.strToBuf(args.contractId))
      ) {
        throw new Error(
          `Action can only be proposed once contract '${args.contractId}' has been accepted.`
        );
      }
    },
    function (args) {
      let item = {};
      item.status = "disabled";
      ccf.kv[telemetryStatusTable].set(
        ccf.strToBuf(args.contractId),
        ccf.jsonCompatibleToBuf(item)
      );
    }
  )
);
actions.set(
  "set_contract_runtime_options_enable_logging",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");

      if (
        !ccf.kv[accepted_contracts_table].has(ccf.strToBuf(args.contractId))
      ) {
        throw new Error(
          `Action can only be proposed once contract '${args.contractId}' has been accepted.`
        );
      }
    },
    function (args) {
      let item = {};
      item.status = "enabled";
      ccf.kv[loggingStatusTable].set(
        ccf.strToBuf(args.contractId),
        ccf.jsonCompatibleToBuf(item)
      );
    }
  )
);
actions.set(
  "set_contract_runtime_options_disable_logging",
  new Action(
    function (args) {
      checkType(args.contractId, "string", "contractId");

      if (
        !ccf.kv[accepted_contracts_table].has(ccf.strToBuf(args.contractId))
      ) {
        throw new Error(
          `Action can only be proposed once contract '${args.contractId}' has been accepted.`
        );
      }
    },
    function (args) {
      let item = {};
      item.status = "disabled";
      ccf.kv[loggingStatusTable].set(
        ccf.strToBuf(args.contractId),
        ccf.jsonCompatibleToBuf(item)
      );
    }
  )
);

const runtimeOptionsTable = "public:ccf.gov.cgs_runtime_options";
const runtimeOptions = [
  "autoapprove-constitution-proposal",
  "autoapprove-jsapp-proposal",
  "autoapprove-deploymentspec-proposal",
  "autoapprove-cleanroompolicy-proposal"
];
actions.set(
  "set_cgs_runtime_options",
  new Action(
    function (args) {
      checkType(args.option, "string", "option");
      checkType(args.status, "string", "status");

      if (!runtimeOptions.includes(args.option)) {
        throw new Error(`The option '${args.option}' is not an allowed option`);
      }

      if (args.status !== "enabled" && args.status !== "disabled") {
        throw new Error(`The value '${args.status}' is not supported`);
      }
    },
    function (args) {
      let item = {};
      item.status = args.status;
      ccf.kv[runtimeOptionsTable].set(
        ccf.strToBuf(args.option),
        ccf.jsonCompatibleToBuf(item)
      );
    }
  )
);

const accepted_documents_table = "public:ccf.gov.accepted_documents";
actions.set(
  "set_document",
  new Action(
    function (args) {
      checkType(args.documentId, "string", "documentId");
      checkType(args.document, "object", "document");
    },
    function (args, proposalId) {
      let document_item = {};
      document_item.data = args.document.data;
      document_item.contractId = args.document.contractId;

      // Save the final votes tally along with the accepted document for reporting purposes.
      let proposals = ccf.kv["public:ccf.gov.proposals_info"];
      let proposalInfoBuffer = proposals.get(ccf.strToBuf(proposalId));
      if (proposalInfoBuffer === undefined) {
        throw new Error(`Can't find proposal info for ${proposalId}`);
      }

      const proposalInfo = ccf.bufToJsonCompatible(proposalInfoBuffer);
      document_item.proposalId = proposalId;
      document_item.finalVotes = proposalInfo.final_votes;

      ccf.kv[accepted_documents_table].set(
        ccf.strToBuf(args.documentId),
        ccf.jsonCompatibleToBuf(document_item)
      );
    }
  )
);
