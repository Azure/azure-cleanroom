package admission

// Request represents an admission request for a pod
type Request struct {
	// Namespace is the namespace of the pod
	Namespace string

	// Name is the name of the pod
	Name string

	// Pod is the parsed pod object
	Pod map[string]interface{}

	// RawPod is the raw JSON bytes of the pod
	RawPod []byte
}

// Decision represents the admission decision
type Decision struct {
	// Allowed indicates whether the pod is allowed
	Allowed bool

	// Reason is the reason for the decision (especially for denials)
	Reason string

	// MutatedPod is the mutated pod if any mutations were applied
	MutatedPod map[string]interface{}

	// Warnings are non-fatal warnings to log
	Warnings []string
}

// Controller is the interface for admission controllers
type Controller interface {
	// Admit evaluates a pod and returns an admission decision
	Admit(req *Request) *Decision

	// Name returns the name of the admission controller
	Name() string
}

// Allow creates an allowed decision
func Allow(reason string) *Decision {
	return &Decision{
		Allowed: true,
		Reason:  reason,
	}
}

// Deny creates a denied decision
func Deny(reason string) *Decision {
	return &Decision{
		Allowed: false,
		Reason:  reason,
	}
}

// AllowWithWarnings creates an allowed decision with warnings
func AllowWithWarnings(reason string, warnings ...string) *Decision {
	return &Decision{
		Allowed:  true,
		Reason:   reason,
		Warnings: warnings,
	}
}

// AllowWithMutation creates an allowed decision with a mutated pod
func AllowWithMutation(reason string, mutatedPod map[string]interface{}) *Decision {
	return &Decision{
		Allowed:    true,
		Reason:     reason,
		MutatedPod: mutatedPod,
	}
}
