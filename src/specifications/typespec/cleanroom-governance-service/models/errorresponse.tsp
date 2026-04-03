namespace Cleanroom.Governance;

/**
 * Error response
 */
model ErrorResponse {
  /** Error details */
  error: ErrorDetails;
}

/**
 * Error details
 */
model ErrorDetails {
  /** Error code */
  code: string;

  /** Error message */
  message: string;
}
