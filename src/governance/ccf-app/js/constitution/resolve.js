// Defines which of the members are CGS operators.
function isCgsOperator(memberId) {
  const info = getMemberInfo(memberId);
  return (
    info.member_data &&
    info.member_data.cgsRoles &&
    info.member_data.cgsRoles.cgsOperator === "true"
  );
}

function isContractOperator(memberId) {
  const info = getMemberInfo(memberId);
  return (
    info.member_data &&
    info.member_data.cgsRoles &&
    info.member_data.cgsRoles.contractOperator === "true"
  );
}

function getRuntimeOption(option) {
  const key = ccf.strToBuf(option);
  const value = ccf.kv[runtimeOptionsTable].get(key);
  if (value !== undefined) {
    return ccf.bufToJsonCompatible(value);
  }
}

function isRuntimeOptionEnabled(option) {
  const item = getRuntimeOption(option);
  return item && item.status === "enabled";
}

// Defines actions that can be passed with sole operator input.
function canCgsOperatorPass(action) {
  const allowedOperatorActions = ["set_constitution", "set_js_app"];

  const actionToOption = {
    set_constitution: "autoapprove-constitution-proposal",
    set_js_app: "autoapprove-jsapp-proposal"
  };

  if (
    allowedOperatorActions.includes(action.name) &&
    isRuntimeOptionEnabled(actionToOption[action.name])
  ) {
    return true;
  }

  return false;
}

// Defines actions that can be passed with sole operator input.
function canContractOperatorPass(action) {
  const allowedOperatorActions = [
    "set_deployment_spec",
    "set_clean_room_policy"
  ];

  const actionToOption = {
    set_deployment_spec: "autoapprove-deploymentspec-proposal",
    set_clean_room_policy: "autoapprove-cleanroompolicy-proposal"
  };

  if (
    allowedOperatorActions.includes(action.name) &&
    isRuntimeOptionEnabled(actionToOption[action.name])
  ) {
    return true;
  }

  return false;
}

// Defines actions that can be passed without needing any votes.
function canAutoAccept(action) {
  const autoAcceptActions = [
    "set_contract_runtime_options_disable_telemetry",
    "set_contract_runtime_options_disable_logging"
  ];

  if (autoAcceptActions.includes(action.name)) {
    return true;
  }
}

// Defines actions that can be vetoed by any member and hence rejected.
function canVetoAction(action) {
  const vetoActions = [
    "set_contract",
    "set_clean_room_policy",
    "set_deployment_spec",
    "set_contract_runtime_options_enable_logging",
    "set_contract_runtime_options_enable_telemetry",
    "set_member_document"
  ];

  if (vetoActions.includes(action.name)) {
    return true;
  }
}

// Defines actions that need Accepted but yet Active members to also become Active and then vote.
function needsAcceptedMembersToVote(action) {
  const acceptedMembersNeedToVoteActions = ["set_contract"];

  if (acceptedMembersNeedToVoteActions.includes(action.name)) {
    return true;
  }
}

function getActiveCgsMemberCount() {
  let activeMemberCount = 0;
  ccf.kv["public:ccf.gov.members.info"].forEach((v, k) => {
    const memberId = ccf.bufToStr(k);
    const info = ccf.bufToJsonCompatible(v);
    if (
      info.status === "Active" &&
      !isOperator(memberId) &&
      !isRecoveryOperator(memberId) &&
      !isCgsOperator(memberId) &&
      !isContractOperator(memberId)
    ) {
      activeMemberCount++;
    }
  });

  return activeMemberCount;
}

function getAcceptedCgsMemberCount() {
  let acceptedMemberCount = 0;
  ccf.kv["public:ccf.gov.members.info"].forEach((v, k) => {
    const memberId = ccf.bufToStr(k);
    const info = ccf.bufToJsonCompatible(v);
    if (
      info.status === "Accepted" &&
      !isOperator(memberId) &&
      !isRecoveryOperator(memberId) &&
      !isCgsOperator(memberId) &&
      !isContractOperator(memberId)
    ) {
      acceptedMemberCount++;
    }
  });

  return acceptedMemberCount;
}

const is_set_contract = (element) => element.name == "set_contract";
const is_set_member_document = (element) =>
  element.name == "set_member_document";

export function resolve(proposal, proposerId, votes) {
  // Operators proposing operator changes can accept them without a vote.
  const resolution = operatorResolve(proposal, proposerId, votes);
  if (resolution == "Accepted") {
    return "Accepted";
  }

  // Custom logic.
  const actions = JSON.parse(proposal)["actions"];

  const canVeto = actions.some(canVetoAction);
  if (canVeto && votes.some((v) => !v.vote)) {
    // Every member has the ability to veto certain proposal types.
    // If they vote against it, it is rejected.
    return "Rejected";
  }

  const anySetContractChange = actions.some(is_set_contract);
  if (anySetContractChange) {
    // A set_contract action cannot be mixed with any other action type.
    if (!actions.every(is_set_contract)) {
      console.log(
        "rejecting proposal as set_contract action should not be mixed with any other actions in the same proposal"
      );
      return "Rejected";
    }

    // All set_contract actions should have different contractId values.
    const uniqueContracts = new Set(actions.map((v) => v.args.contractId));
    if (uniqueContracts.size < actions.length) {
      console.log(
        "rejecting proposal as multiple set_contract actions with the same contractId were found"
      );
      return "Rejected";
    }

    // A set_contract contractId should not be already present under a different proposal.
    if (
      actions.some((action) =>
        contractId_proposal_already_exists(action.args.contractId)
      )
    ) {
      console.log(
        "rejecting proposal as a contractId already exists under a different proposal"
      );
      return "Rejected";
    }
  }

  const anySetDocumentChange = actions.some(is_set_member_document);
  if (anySetDocumentChange) {
    // A set_member_document documentId should not be already present under a different proposal.
    if (
      actions.some((action) =>
        documentId_proposal_already_exists(action.args.documentId)
      )
    ) {
      console.log(
        "rejecting proposal as a documentId already exists under a different proposal"
      );
      return "Rejected";
    }
  }

  const memberVoteCount = votes.filter(
    (v) =>
      v.vote &&
      !isOperator(v.member_id) &&
      !isRecoveryOperator(v.member_id) &&
      !isCgsOperator(v.member_id) &&
      !isContractOperator(v.member_id)
  ).length;

  // Certain proposals need no voting by any member (like disabling contract logging/telemetry).
  const autoAccept = actions.every(canAutoAccept);
  if (autoAccept) {
    return "Accepted";
  }

  const activeMemberCount = getActiveCgsMemberCount();
  const acceptedMemberCount = getAcceptedCgsMemberCount();

  if (actions.some(needsAcceptedMembersToVote)) {
    // Certain proposals require accepted members to also participate and not just active members.
    // So accepted members need to become active and then as active members need to accept for
    // any proposal to go thru.
    if (memberVoteCount == activeMemberCount && acceptedMemberCount == 0) {
      return "Accepted";
    } else if (memberVoteCount == activeMemberCount) {
      console.log(
        `Not yet accepting proposal as this proposal requires ` +
          `not just all active members but also members in accepted state to become ` +
          `active and vote. acceptedMembersCount: ${acceptedMemberCount}`
      );
    }
  } else if (memberVoteCount == activeMemberCount) {
    return "Accepted";
  }

  // A proposal is a CGS operator change if it's only applying CGS operator actions.
  const isCgsOperatorChange = actions.every(canCgsOperatorPass);

  // Operators proposing operator changes can accept them without a vote.
  if (isCgsOperatorChange && isCgsOperator(proposerId)) {
    return "Accepted";
  }

  // A proposal is a contract operator change if it's only applying contract operator actions.
  const isContractOperatorChange = actions.every(canContractOperatorPass);

  // Operators proposing operator changes can accept them without a vote.
  if (isContractOperatorChange && isContractOperator(proposerId)) {
    return "Accepted";
  }

  return "Open";
}

function contractId_proposal_already_exists(contractId) {
  // Check if the contract is already accepted or open.
  if (
    ccf.kv["public:ccf.gov.accepted_contracts"].has(ccf.strToBuf(contractId))
  ) {
    return true;
  }

  var proposalCount = 0;
  ccf.kv["public:ccf.gov.proposals"].forEach((v, k) => {
    const proposal = ccf.bufToJsonCompatible(v);
    proposal.actions.forEach((value) => {
      if (value.name === "set_contract") {
        const args = value.args;
        if (args.contractId === contractId) {
          const proposalInfo = ccf.bufToJsonCompatible(
            ccf.kv["public:ccf.gov.proposals_info"].get(k)
          );
          if (proposalInfo.state == "Open") {
            proposalCount++;
            if (proposalCount > 1) {
              return false;
            }
          }
        }
      }
    });
  });

  return proposalCount > 1;
}

function documentId_proposal_already_exists(documentId) {
  // Check if the document is already accepted or open.
  if (
    ccf.kv["public:ccf.gov.accepted_member_documents"].has(
      ccf.strToBuf(documentId)
    )
  ) {
    return true;
  }

  var proposalCount = 0;
  ccf.kv["public:ccf.gov.proposals"].forEach((v, k) => {
    const proposal = ccf.bufToJsonCompatible(v);
    proposal.actions.forEach((value) => {
      if (value.name === "set_member_document") {
        const args = value.args;
        if (args.documentId === documentId) {
          const proposalInfo = ccf.bufToJsonCompatible(
            ccf.kv["public:ccf.gov.proposals_info"].get(k)
          );
          if (proposalInfo.state == "Open") {
            proposalCount++;
            if (proposalCount > 1) {
              return false;
            }
          }
        }
      }
    });
  });

  return proposalCount > 1;
}
