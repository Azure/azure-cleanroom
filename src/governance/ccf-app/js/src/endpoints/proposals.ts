import * as ccfapp from "@microsoft/ccf-app";
import { ccf } from "@microsoft/ccf-app/global";
import { ProposalInfoItem } from "../models/kvstoremodels";
import { GetProposalResponse, Proposal } from "../models/proposalmodels";
import { parseRequestQuery } from "../utils/utils";

// Code adapted from https://raw.githubusercontent.com/microsoft/ccf-app-samples/main/auditable-logging-app/src/endpoints/log.ts

const proposalsInfoStore = ccfapp.typedKv(
  "public:ccf.gov.proposals_info",
  ccfapp.string,
  ccfapp.arrayBuffer
);

export function getProposalHistorical(
  request: ccfapp.Request
): ccfapp.Response<GetProposalResponse> {
  const proposalId = request.params.proposalId;
  const parsedQuery = parseRequestQuery(request);
  let { from_seqno, to_seqno, max_seqno_per_page } = parsedQuery;
  if (from_seqno !== undefined) {
    from_seqno = parseInt(from_seqno);
    if (isNaN(from_seqno)) {
      throw new Error("from_seqno is not an integer");
    }
  } else {
    // If no from_seqno is specified, defaults to very first transaction in the ledger.
    from_seqno = 1;
  }

  if (to_seqno !== undefined) {
    to_seqno = parseInt(to_seqno);
    if (isNaN(to_seqno)) {
      throw new Error("to_seqno is not an integer");
    }
  } else {
    // If no end point is specified, use the last time this ID was written to.
    const lastWriteVersion =
      proposalsInfoStore.getVersionOfPreviousWrite(proposalId);
    if (lastWriteVersion !== undefined) {
      to_seqno = lastWriteVersion;
    } else {
      // If there's no last written version, it may have never been
      // written but may simply be currently deleted. Use current commit
      // index as end point to ensure we include any deleted entries.
      to_seqno = ccf.consensus.getLastCommittedTxId().seqno;
    }
  }

  // Range must be in order.
  if (to_seqno < from_seqno) {
    throw new Error("to_seqno must be >= from_seqno");
  }

  if (max_seqno_per_page !== undefined) {
    max_seqno_per_page = parseInt(max_seqno_per_page);
    if (isNaN(max_seqno_per_page)) {
      throw new Error("max_seqno_per_page is not an integer");
    }
  } else {
    // If no max_seqno_per_page is specified, defaults to 2000.
    max_seqno_per_page = 2000;
  }

  // End of range must be committed.
  let isCommitted = false;
  const viewOfFinalSeqno = ccf.consensus.getViewForSeqno(to_seqno);
  if (viewOfFinalSeqno !== null) {
    const txStatus = ccf.consensus.getStatusForTxId(viewOfFinalSeqno, to_seqno);
    isCommitted = txStatus === "Committed";
  }
  if (!isCommitted) {
    throw new Error("End of range must be committed");
  }

  const rangeBegin = from_seqno;
  const rangeEnd = Math.min(to_seqno, rangeBegin + max_seqno_per_page);

  // Compute a deterministic handle for the range request.
  // Note: Instead of ccf.digest, an equivalent of std::hash should be used.
  const makeHandle = (begin, end, id) => {
    const cacheKey = `${begin}-${end}-${id}`;
    const digest = ccf.crypto.digest("SHA-256", ccf.strToBuf(cacheKey));
    const handle = new DataView(digest).getUint32(0);
    return handle;
  };
  const handle = makeHandle(rangeBegin, rangeEnd, proposalId);

  // Fetch the requested range.
  const expirySeconds = 1800;
  const states = ccf.historical.getStateRange(
    handle,
    rangeBegin,
    rangeEnd,
    expirySeconds
  );
  if (states === null) {
    return {
      statusCode: 202,
      headers: {
        "retry-after": "1"
      }
    };
  }

  // Process the fetched states.
  const entries: Proposal[] = [];
  for (const state of states) {
    const proposalInfoHistorical = ccfapp.typedKv(
      state.kv["public:ccf.gov.proposals_info"],
      ccfapp.string,
      ccfapp.arrayBuffer
    );
    const item = proposalInfoHistorical.get(proposalId);
    if (item !== undefined) {
      const p = ccf.bufToJsonCompatible(item) as ProposalInfoItem;
      entries.push({
        seqno: parseInt(state.transactionId.split(".")[1]),
        proposalId: proposalId,
        proposalState: p.state
      });
    }
    // This response does not include any entry when the given key wasn't
    // modified at this seqno. It could instead indicate that the store
    // was checked with an empty tombstone object, but this approach gives
    // smaller responses.
  }

  // If this didn't cover the total requested range, begin fetching the
  // next page and tell the caller how to retrieve it.
  let nextLink;
  if (rangeEnd != to_seqno) {
    const next_page_start = rangeEnd + 1;
    const next_page_end = Math.min(
      to_seqno,
      next_page_start + max_seqno_per_page
    );
    const next_page_handle = makeHandle(
      next_page_start,
      next_page_end,
      proposalId
    );
    ccf.historical.getStateRange(
      next_page_handle,
      next_page_start,
      next_page_end,
      expirySeconds
    );

    // NB: This path tells the caller to continue to ask until the end of
    // the range, even if the next response is paginated.
    const nextLinkPrefix = `/app/proposals/${proposalId}/historical`;
    nextLink = `${nextLinkPrefix}?from_seqno=${next_page_start}&to_seqno=${to_seqno}`;
  }

  // Assume this response makes it all the way to the client, and
  // they're finished with it, so we can drop the retrieved state. Note: Consider if
  // this may be driven by a separate client request or an LRU.
  ccf.historical.dropCachedStates(handle);

  return {
    body: {
      value: entries,
      nextLink: nextLink
    }
  };
}
