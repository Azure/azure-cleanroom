// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { CvmSnpAttestationInput } from "../models";
import { ISnpCvmAttestationReport } from "./ISnpCvmAttestationReport";

export class SnpCvmAttestationClaims {
  constructor(public input: CvmSnpAttestationInput) {}

  public getClaims(): ISnpCvmAttestationReport {
    const reportClaims: ISnpCvmAttestationReport = {};
    const pcrs = this.input?.evidence?.pcrs;
    if (pcrs === undefined) {
      return reportClaims;
    }

    for (const [key, val] of Object.entries(pcrs)) {
      if (val !== undefined) {
        reportClaims[`pcr${key}`] = val;
      }
    }

    return reportClaims;
  }
}
