package controller

import (
	"errors"
	"fmt"

	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	ctrlclient "sigs.k8s.io/controller-runtime/pkg/client"
)

// permanentError wraps an error that should not be retried
// by the controller reconcile loop (e.g. CRD validation
// failures, RBAC rejections).
type permanentError struct {
	err error
}

func (e *permanentError) Error() string {
	return e.err.Error()
}

func (e *permanentError) Unwrap() error {
	return e.err
}

// newPermanentError wraps err as a permanentError.
func newPermanentError(err error) error {
	return &permanentError{err: err}
}

// isPermanentError returns true if the error (or any error
// in its chain) is a permanentError.
func isPermanentError(err error) bool {
	var pe *permanentError
	return errors.As(err, &pe)
}

// maxConditionMessageLen is the maximum length for a
// Kubernetes status condition message. The API server
// rejects messages longer than 32768 bytes; we cap well
// below that to keep output readable.
const maxConditionMessageLen = 8192

// truncateMessage truncates msg to maxLen, appending
// a suffix to indicate truncation occurred.
func truncateMessage(msg string, maxLen int) string {
	if len(msg) <= maxLen {
		return msg
	}
	const suffix = " ... [truncated]"
	return msg[:maxLen-len(suffix)] + suffix
}

// wrapPermanentAPIError checks whether err is a permanent
// API server rejection (Invalid, Forbidden) and if so
// wraps it as a permanentError with the given message.
// Transient errors are returned as-is wrapped with msg.
func wrapPermanentAPIError(
	err error, msg string,
) error {
	if apierrors.IsInvalid(err) ||
		apierrors.IsForbidden(err) {
		return newPermanentError(
			fmt.Errorf("%s: %w", msg, err),
		)
	}
	return fmt.Errorf("%s: %w", msg, err)
}

// hasOwnerReference returns true if owned has an
// ownerReference pointing to owner.
func hasOwnerReference(
	owned metav1.Object,
	owner ctrlclient.Object,
) bool {
	for _, ref := range owned.GetOwnerReferences() {
		if ref.UID == owner.GetUID() {
			return true
		}
	}
	return false
}
