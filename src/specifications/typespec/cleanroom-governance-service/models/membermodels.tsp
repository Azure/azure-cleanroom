namespace Cleanroom.Governance;

/**
 * Member info
 */
model MemberInfo {
  /** Member data */
  @encodedName("application/json", "member_data")
  memberData: MemberData;
}

/**
 * Member data
 */
model MemberData {
  /** Is operator */
  isOperator: boolean;

  /** Tenant ID */
  tenantId: string;
}
