function getMemberInfo(memberId) {
  const key = ccf.strToBuf(memberId);
  const value = ccf.kv["public:ccf.gov.members.info"].get(key);
  const info = ccf.bufToJsonCompatible(value);
  return info;
}

// Defines which of the members are operators.
function isOperator(memberId) {
  const info = getMemberInfo(memberId);
  return info.member_data && info.member_data.isOperator;
}

function isRecoveryOperator(memberId) {
  const info = getMemberInfo(memberId);
  return info.member_data && info.member_data.isRecoveryOperator;
}

function getActiveMemberCount() {
  let activeMemberCount = 0;
  ccf.kv["public:ccf.gov.members.info"].forEach((v, k) => {
    const memberId = ccf.bufToStr(k);
    const info = ccf.bufToJsonCompatible(v);
    if (
      info.status === "Active" &&
      !isOperator(memberId) &&
      !isRecoveryOperator(memberId)
    ) {
      activeMemberCount++;
    }
  });

  return activeMemberCount;
}

function getAcceptedMemberCount() {
  let acceptedMemberCount = 0;
  ccf.kv["public:ccf.gov.members.info"].forEach((v, k) => {
    const memberId = ccf.bufToStr(k);
    const info = ccf.bufToJsonCompatible(v);
    if (
      info.status === "Accepted" &&
      !isOperator(memberId) &&
      !isRecoveryOperator(memberId)
    ) {
      acceptedMemberCount++;
    }
  });

  return acceptedMemberCount;
}

// Defines actions that can be passed with sole operator input.
function canOperatorPass(action) {
  const allowedOperatorActions = [
    "add_snp_uvm_endorsement", // To add a trusted UVM endorsement (Azure deployment only).
    "remove_ca_cert_bundle", // To manage CAs for OpenID configuration endpoints (i.e. when using Entra).
    "remove_jwt_issuer", // Same as above, but to manage the issuers themselves.
    "remove_node",
    "remove_snp_uvm_endorsement", // Goes along with add_snp_uvm_endorsement
    "set_ca_cert_bundle", // For OpenID/OAuth/Entra to update the CA bundle used to authenticate the configuration endpoint.
    "set_jwt_issuer", // Same as set_ca_cert and remove_jwt_issuer, this is necessary to manage/update IdPs.
    "set_node_certificate_validity",
    "set_node_data",
    "set_service_certificate_validity",
    "set_recovery_threshold", // Possible to avoid by having recovery svc member pre-created?
    "transition_node_to_trusted",
    "transition_service_to_open",
    "trigger_ledger_chunk",
    "trigger_snapshot"
  ];

  if (allowedOperatorActions.includes(action.name)) {
    return true;
  }
  // Additionally, operators can add or retire other operators.
  if (action.name === "set_member") {
    const memberData = action.args["member_data"];
    if (memberData && memberData.isOperator) {
      return true;
    }
  } else if (action.name === "remove_member") {
    const memberId = ccf.pemToId(action.args.cert);
    if (isOperator(memberId)) {
      return true;
    }
  }
  return false;
}

function operatorResolve(proposal, proposerId, votes) {
  const actions = JSON.parse(proposal)["actions"];

  // A proposal is an operator change if it's only applying operator actions.
  const isOperatorChange = actions.every(canOperatorPass);

  // Operators proposing operator changes can accept them without a vote.
  if (isOperatorChange && isOperator(proposerId)) {
    return "Accepted";
  }

  return "Open";
}
