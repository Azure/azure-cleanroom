import * as ccfapp from "@microsoft/ccf-app";
import { ccf } from "@microsoft/ccf-app/global";
import {
  SnpAttestationResult,
  verifySnpAttestation
} from "../attestation/snpattestation";
import { ErrorResponse } from "../models/errorresponse";
import {
  b64ToBuf,
  parseRequestQuery,
  validateCallerAuthorized,
  verifyReportData,
  verifySignature
} from "../utils/utils";
import { Event, GetEventsResponse, PutEventRequest } from "../models";
import { EventStoreItem } from "../models/kvstoremodels";

// Code adapted from https://raw.githubusercontent.com/microsoft/ccf-app-samples/main/auditable-logging-app/src/endpoints/log.ts

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function getIdFromQuery(parsedQuery: any, contractId: string): string {
  if (parsedQuery.id === undefined) {
    return contractId;
  }
  return parsedQuery.id;
}

function getEventsMapName(contractId: string, scope: string): string {
  if (!scope) {
    return "public:events-" + contractId;
  }

  return "public:events-" + contractId + "-" + scope;
}

export function getEvent(
  request: ccfapp.Request
): ccfapp.Response<GetEventsResponse> | ccfapp.Response<ErrorResponse> {
  const error = validateCallerAuthorized(request);
  if (error !== undefined) {
    return error;
  }

  const contractId = request.params.contractId;
  const parsedQuery = parseRequestQuery(request);
  const id = getIdFromQuery(parsedQuery, contractId);
  let { from_seqno, to_seqno, max_seqno_per_page, scope } = parsedQuery;
  if (!scope) {
    scope = "";
  }

  const eventsMap = ccfapp.typedKv(
    getEventsMapName(contractId, scope),
    ccfapp.string,
    ccfapp.json<EventStoreItem>()
  );

  // If no sequence numbers are specified then return the latest value, if any.
  if (!from_seqno && !to_seqno) {
    if (!eventsMap.has(id)) {
      return {
        body: {
          value: []
        }
      };
    }

    const item = eventsMap.get(id);
    return {
      body: {
        value: [
          {
            scope: scope,
            id: id,
            seqno: eventsMap.getVersionOfPreviousWrite(id),
            timestamp: item.timestamp,
            timestamp_iso: new Date(Number(item.timestamp)).toISOString(),
            data: item.data
          }
        ]
      }
    };
  }

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
    const lastWriteVersion = eventsMap.getVersionOfPreviousWrite(id);
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
  const handle = makeHandle(rangeBegin, rangeEnd, id);

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
  const entries: Event[] = [];
  for (const state of states) {
    const eventsMapHistorical = ccfapp.typedKv(
      state.kv[getEventsMapName(contractId, scope)],
      ccfapp.string,
      ccfapp.json<EventStoreItem>()
    );
    const item = eventsMapHistorical.get(id);
    if (item !== undefined) {
      entries.push({
        scope: scope,
        id: id,
        seqno: parseInt(state.transactionId.split(".")[1]),
        timestamp: item.timestamp,
        timestamp_iso: new Date(Number(item.timestamp)).toISOString(),
        data: item.data
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
    const next_page_handle = makeHandle(next_page_start, next_page_end, id);
    ccf.historical.getStateRange(
      next_page_handle,
      next_page_start,
      next_page_end,
      expirySeconds
    );

    // NB: This path tells the caller to continue to ask until the end of
    // the range, even if the next response is paginated.
    const nextLinkPrefix = `/app/contracts/${contractId}/events`;
    nextLink = `${nextLinkPrefix}?from_seqno=${next_page_start}&to_seqno=${to_seqno}&id=${id}`;
    if (scope !== undefined) {
      nextLink += `&scope=${scope}`;
    }
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

export function putEvent(
  request: ccfapp.Request<PutEventRequest>
): ccfapp.Response {
  const body = request.body.json();
  if (!body.attestation) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "AttestationMissing",
        "Attestation payload must be supplied."
      )
    };
  }

  if (!body.sign) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "SignatureMissing",
        "Signature payload must be supplied."
      )
    };
  }

  if (!body.timestamp) {
    return {
      statusCode: 400,
      body: new ErrorResponse("TimestampMissing", "Timestamp must be supplied.")
    };
  }

  // Similar logic below (but milliseconds instead of seconds) as created_at parameter value for
  // CCF governance proposals.
  // timestamp, submitted as a integer number of milliseconds since epoch, is converted to a
  // decimal representation in ASCII, stored as a string, and
  // compared alphanumerically. This is partly to keep governance as
  // text-based as possible, to facilitate audit, but also to be able
  // to benefit from future planned ordering support in the KV. To
  // compare correctly, the string representation needs to be padded
  // with leading zeroes, and must therefore not exceed a fixed
  // digit width. 13 digits is enough to last until November 2286,
  // ie. long enough.
  const timestampNumber = Number(body.timestamp);
  if (timestampNumber > 9999999999999) {
    return {
      statusCode: 400,
      body: new ErrorResponse(
        "TimestampTooLarge",
        "Timestamp value is too large."
      )
    };
  }

  // First validate attestation report.
  const contractId = request.params.contractId;
  let snpAttestationResult: SnpAttestationResult;
  try {
    snpAttestationResult = verifySnpAttestation(contractId, body.attestation);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("VerifySnpAttestationFailed", e.message)
    };
  }

  //  Then validate the report data value.
  try {
    verifyReportData(snpAttestationResult, body.sign.publicKey);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("ReportDataMismatch", e.message)
    };
  }

  // Attestation report and report data values are verified. Now check the signature.
  const data: ArrayBuffer = b64ToBuf(body.data);
  try {
    verifySignature(body.sign, data);
  } catch (e) {
    return {
      statusCode: 400,
      body: new ErrorResponse("SignatureMismatch", e.message)
    };
  }

  // All verifications pass. Now insert the event.
  const parsedQuery = parseRequestQuery(request);
  const id = getIdFromQuery(parsedQuery, contractId);
  const { scope } = parsedQuery;
  const eventsMap = ccfapp.typedKv(
    getEventsMapName(contractId, scope),
    ccfapp.string,
    ccfapp.json<EventStoreItem>()
  );

  const jsonObject = JSON.parse(ccf.bufToStr(data));
  const timestamp = body.timestamp.padStart(13, "0");
  eventsMap.set(id, { timestamp: timestamp, data: jsonObject });
  return {
    statusCode: 204
  };
}
