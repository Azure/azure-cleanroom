import { ErrorResponse as IErrorResponse, ErrorDetails } from "../models";

/**
 * ErrorResponse class implementation that wraps the auto-generated interface.
 * This class provides a convenient constructor for creating error responses.
 */
export class ErrorResponse implements IErrorResponse {
  error: ErrorDetails;

  constructor(code: string, message: string) {
    this.error = {
      code: code,
      message: message,
    };
  }
}
