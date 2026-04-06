import {
  getContractCleanRoomPolicyProps,
  isEmpty,
  getDelegateCleanRoomPolicyProps,
  matchPolicyClaims
} from "../utils/utils";

export function verifyJwtClaims(
  contractId: string,
  payloadClaims: any,
  delegatedPolicies?: string[]
) {
  // Get the clean room policy.
  const cleanroomPolicy = getContractCleanRoomPolicyProps(contractId);

  console.log(
    `Clean room policy: ${JSON.stringify(cleanroomPolicy)}, keys: ${Object.keys(
      cleanroomPolicy
    )}, keys: ${Object.keys(cleanroomPolicy).length}`
  );

  if (isEmpty(cleanroomPolicy)) {
    throw Error(
      "The clean room policy is missing. Please propose a new clean room policy."
    );
  }

  // First, try to match with the main cleanroom policy.
  let matchFound: boolean = matchPolicyClaims(cleanroomPolicy, payloadClaims);

  // If no match found and we have delegated policies, iterate through them to find a match.
  if (!matchFound && delegatedPolicies && delegatedPolicies.length > 0) {
    for (const delegatedPolicyId of delegatedPolicies) {
      const delegatedPolicy =
        getDelegateCleanRoomPolicyProps(delegatedPolicyId);
      if (!isEmpty(delegatedPolicy)) {
        console.log(`Checking delegated policy: ${delegatedPolicyId}`);
        matchFound = matchPolicyClaims(delegatedPolicy, payloadClaims);
        if (matchFound) {
          console.log(
            `Match found with delegated policy: ${delegatedPolicyId}`
          );
          break;
        }
      }
    }
  }

  // If still no match found, throw an error
  if (!matchFound) {
    throw Error(
      `Attestation claims do not match the contract policy${delegatedPolicies ? `, or the delegated policies` : ""}.`
    );
  }
}
