package controller

import (
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/types"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"

	"context"
)

// controllerOwnerUID returns the UID of the controller owner
// from the object's ownerReferences. Returns empty string if
// no controller owner is found.
func controllerOwnerUID(
	obj metav1.ObjectMetaAccessor,
) types.UID {
	for _, ref := range obj.GetObjectMeta().
		GetOwnerReferences() {
		if ref.Controller != nil && *ref.Controller {
			return ref.UID
		}
	}
	return ""
}

// isStaleResource checks whether the referenced resource
// belongs to a different Environment than the caller.
// callerEnvUID is the UID of the Environment that owns the
// caller (may be indirect, e.g. via WorkloadGovernance).
// The ref is expected to be directly owned by an
// Environment, so controllerOwnerUID(ref) gives the
// Environment UID. If stale, it logs a message and returns
// true so the caller can wait for garbage collection.
func isStaleResource(
	ctx context.Context,
	callerEnvUID types.UID,
	ref metav1.ObjectMetaAccessor,
	refKind, refName string,
) bool {
	refEnvUID := controllerOwnerUID(ref)
	if callerEnvUID == "" || refEnvUID == "" {
		return false
	}
	if callerEnvUID == refEnvUID {
		return false
	}
	log := ctrllog.FromContext(ctx)
	log.Info(
		refKind+" owned by different Environment,"+
			" waiting for GC",
		"resource", refName,
		"callerEnvUID", callerEnvUID,
		"resourceEnvUID", refEnvUID,
	)
	return true
}
