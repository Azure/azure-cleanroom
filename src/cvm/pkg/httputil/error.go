// Package httputil provides shared HTTP helpers for the attestation services.
package httputil

import (
	"encoding/json"
	"net/http"
)

// ErrorResponse is the JSON error envelope returned on failure.
type ErrorResponse struct {
	Error ErrorDetail `json:"error"`
}

// ErrorDetail holds the error code and human-readable message.
type ErrorDetail struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

// WriteError writes a structured JSON error response.
func WriteError(w http.ResponseWriter, status int, code, message string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(ErrorResponse{
		Error: ErrorDetail{Code: code, Message: message},
	})
}
