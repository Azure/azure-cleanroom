import "../models.tsp";

using Azure.Cleanroom.Governance;

namespace Azure.Cleanroom.SDK;

/**
 * RuntimeOptions operations
 */
interface RuntimeOptions {
  /** checkRuntimeOption (AuthPolicies: [member_cert, user_cert, jwt]) */
  @tag("auth:member_cert")
  @tag("auth:user_cert")
  @tag("auth:jwt")
  checkRuntimeOption(option: string): GetRuntimeOptionResponse;
}
