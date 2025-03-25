export class ErrorResponse {
  error: {
    code: string;
    message: string;
  };
  constructor(code: string, message: string) {
    this.error = {
      code: code,
      message: message
    };
  }
}
