namespace Cleanroom.Governance;

/**
 * SNP attestation evidence
 */
model SnpEvidence {
  /** Base64 encoded evidence */
  evidence: string;

  /** Base64 encoded endorsements */
  endorsements: string;

  /** Base64 encoded UVM endorsements */
  @encodedName("application/json", "uvm_endorsements")
  uvmEndorsements: string;

  /** Endorsed TCB version */
  @encodedName("application/json", "endorsed_tcb")
  endorsedTcb?: string;
}

/**
 * TCB (Trusted Computing Base) version
 */
model TcbVersion {
  /** Boot loader version */
  @encodedName("application/json", "boot_loader")
  bootLoader: int32;

  /** TEE version */
  tee: int32;

  /** SNP version */
  snp: int32;

  /** Microcode version */
  microcode: int32;
}

/**
 * SNP attestation result
 */
model SnpAttestationResult {
  /** Attestation details */
  attestation: AttestationDetails;

  /** UVM endorsements */
  @encodedName("application/json", "uvm_endorsements")
  uvmEndorsements?: UvmEndorsements;
}

/**
 * Attestation details
 */
model AttestationDetails {
  /** Attestation version */
  version: int32;

  /** Guest SVN */
  @encodedName("application/json", "guest_svn")
  guestSvn: int32;

  /** Policy configuration */
  policy: PolicyConfig;

  /** Family ID */
  @encodedName("application/json", "family_id")
  familyId: string;

  /** Image ID */
  @encodedName("application/json", "image_id")
  imageId: string;

  /** VMPL level */
  vmpl: int32;

  /** Signature algorithm */
  @encodedName("application/json", "signature_algo")
  signatureAlgo: int32;

  /** Platform version */
  @encodedName("application/json", "platform_version")
  platformVersion: TcbVersion;

  /** Platform information */
  @encodedName("application/json", "platform_info")
  platformInfo: PlatformInfo;

  /** Flags */
  flags: AttestationFlags;

  /** Report data */
  @encodedName("application/json", "report_data")
  reportData: string;

  /** Measurement */
  measurement: string;

  /** Host data */
  @encodedName("application/json", "host_data")
  hostData: string;

  /** ID key digest */
  @encodedName("application/json", "id_key_digest")
  idKeyDigest: string;

  /** Author key digest */
  @encodedName("application/json", "author_key_digest")
  authorKeyDigest: string;

  /** Report ID */
  @encodedName("application/json", "report_id")
  reportId: string;

  /** Report ID MA */
  @encodedName("application/json", "report_id_ma")
  reportIdMa: string;

  /** Reported TCB */
  @encodedName("application/json", "reported_tcb")
  reportedTcb: TcbVersion;

  /** Chip ID */
  @encodedName("application/json", "chip_id")
  chipId: string;

  /** Committed TCB */
  @encodedName("application/json", "committed_tcb")
  committedTcb: TcbVersion;

  /** Current minor version */
  @encodedName("application/json", "current_minor")
  currentMinor: int32;

  /** Current build version */
  @encodedName("application/json", "current_build")
  currentBuild: int32;

  /** Current major version */
  @encodedName("application/json", "current_major")
  currentMajor: int32;

  /** Committed build version */
  @encodedName("application/json", "committed_build")
  committedBuild: int32;

  /** Committed minor version */
  @encodedName("application/json", "committed_minor")
  committedMinor: int32;

  /** Committed major version */
  @encodedName("application/json", "committed_major")
  committedMajor: int32;

  /** Launch TCB */
  @encodedName("application/json", "launch_tcb")
  launchTcb: TcbVersion;

  /** Signature */
  signature: EcdsaSignature;
}

/**
 * Policy configuration
 */
model PolicyConfig {
  /** ABI minor version */
  @encodedName("application/json", "abi_minor")
  abiMinor: int32;

  /** ABI major version */
  @encodedName("application/json", "abi_major")
  abiMajor: int32;

  /** SMT enabled */
  smt: int32;

  /** Migrate MA */
  @encodedName("application/json", "migrate_ma")
  migrateMa: int32;

  /** Debug enabled */
  debug: int32;

  /** Single socket */
  @encodedName("application/json", "single_socket")
  singleSocket: int32;
}

/**
 * Platform information
 */
model PlatformInfo {
  /** SMT enabled */
  @encodedName("application/json", "smt_en")
  smtEn: int32;

  /** TSME enabled */
  @encodedName("application/json", "tsme_en")
  tsmeEn: int32;
}

/**
 * Attestation flags
 */
model AttestationFlags {
  /** Author key enabled */
  @encodedName("application/json", "author_key_en")
  authorKeyEn: int32;

  /** Mask chip key */
  @encodedName("application/json", "mask_chip_key")
  maskChipKey: int32;

  /** Signing key */
  @encodedName("application/json", "signing_key")
  signingKey: int32;
}

/**
 * ECDSA signature
 */
model EcdsaSignature {
  /** R component */
  r: string;

  /** S component */
  s: string;
}

/**
 * UVM endorsements
 */
model UvmEndorsements {
  /** DID */
  did: string;

  /** Feed */
  feed: string;

  /** SVN */
  svn: string;
}
