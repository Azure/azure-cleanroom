// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import { CvmSnpAttestationInput } from "../models";
import { CvmSnpAttestationResult } from "../global.cleanroom";
import { ISnpCvmAttestationReport } from "./ISnpCvmAttestationReport";

const pcrKeys = [
  "0", "1", "2", "3", "4", "5", "6", "7",
  "8", "9", "10", "11", "12", "13", "14", "15",
  "16", "17", "18", "19", "20", "21", "22", "23"
] as const;

export class SnpCvmAttestationClaims {
  constructor(
    public input: CvmSnpAttestationInput,
    public verifierResult: CvmSnpAttestationResult
  ) {}

  public getClaims(): ISnpCvmAttestationReport {
    const reportClaims: ISnpCvmAttestationReport = {};
    const pcrs = this.input?.vtpm?.evidence?.pcrs;
    if (pcrs !== undefined) {
      for (const key of pcrKeys) {
        const val = pcrs[key];
        if (val !== undefined) {
          // PCR fields on the report are all `string`; cast through a
          // narrower record type so the union with GPU fields (numbers /
          // booleans) does not collapse the value type to `never`.
          (reportClaims as Record<string, string>)[`pcr${key}`] = val;
        }
      }
    }

    const gpuClaims = this.verifierResult.gpuClaims;
    if (gpuClaims !== undefined) {
      reportClaims.gpuCount = gpuClaims.gpuCount;
      reportClaims.gpuRIMAppraisal = gpuClaims.gpuRIMAppraisal;
      reportClaims.gpuSecureBoot = gpuClaims.gpuSecureBoot;
      reportClaims.gpuDebugDisabled = gpuClaims.gpuDebugDisabled;
      reportClaims.gpuDriverRIM = gpuClaims.gpuDriverRIM;
      reportClaims.gpuVbiosRIM = gpuClaims.gpuVbiosRIM;
    }

    return reportClaims;
  }
}
