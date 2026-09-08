// A default resolve() implementation that allows operator actions and requires all members to vote
// for anything else.
export function resolve(proposal, proposerId, votes) {
  // The constitution in a CCF application deployment would at least
  // count votes and compare to a threshold of active members or have more custom/advanced logic.
  // In the default constitution we keep it as all operator actions pass automatically while
  // all active members need to vote for anything else.

  // Operators proposing operator changes can accept them without a vote.
  const resolution = operatorResolve(proposal, proposerId, votes);
  if (resolution != "Open") {
    return resolution;
  }

  // Require all active members to vote. If authoring a custom constitution ensure
  // operatorResolve logic above is retained or else operator functionality will get affected.
  const memberVoteCount = votes.filter(
    (v) =>
      v.vote && !isOperator(v.member_id) && !isRecoveryOperator(v.member_id)
  ).length;

  const activeMemberCount = getActiveMemberCount();
  if (memberVoteCount == activeMemberCount) {
    return "Accepted";
  }

  return "Open";
}
