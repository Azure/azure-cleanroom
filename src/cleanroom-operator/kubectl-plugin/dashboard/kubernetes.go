package dashboard

import (
	"context"
	"fmt"
	"sort"
	"time"

	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/apis/meta/v1/unstructured"
	"k8s.io/apimachinery/pkg/runtime/schema"
)

var cleanroomGVRs = []struct {
	Kind     string
	Resource schema.GroupVersionResource
}{
	{"Environment", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "environments",
	}},
	{"Cluster", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "clusters",
	}},
	{"CcfMember", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "ccfmembers",
	}},
	{"CcfNetwork", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "ccfnetworks",
	}},
	{"GovernanceService", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "governanceservices",
	}},
	{"GovernanceContract", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "governancecontracts",
	}},
	{"WorkloadGovernance", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "workloadgovernances",
	}},
	{"ModelRegistration", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "modelregistrations",
	}},
	{"ModelDeployment", schema.GroupVersionResource{
		Group: "cleanroom.azure.com", Version: "v1alpha1",
		Resource: "modeldeployments",
	}},
}

// ResourceInfo holds display info for a single K8s resource.
type ResourceInfo struct {
	Kind       string
	Name       string
	Namespace  string
	Phase      string
	Message    string
	Conditions []ConditionInfo
	Age        time.Time
	OwnerEnv   string
	InfraType  string
	RawObject  map[string]interface{}
}

// ConditionInfo holds a single status condition.
type ConditionInfo struct {
	Type               string
	Status             string
	Reason             string
	Message            string
	LastTransitionTime time.Time
}

// TopologyNode represents a node in the resource DAG.
type TopologyNode struct {
	Resource ResourceInfo
	Children []TopologyNode
}

// EventInfo holds a K8s event.
type EventInfo struct {
	Type      string
	Reason    string
	Message   string
	Object    string
	Age       time.Time
	Count     int32
	Component string
}

// listEnvironments returns all Environment CRs.
func (s *Server) listEnvironments(
	ctx context.Context,
) ([]ResourceInfo, error) {
	gvr := cleanroomGVRs[0].Resource // environments
	list, err := s.dynClient.Resource(gvr).
		Namespace(s.namespace).
		List(ctx, metav1.ListOptions{})
	if err != nil {
		return nil, fmt.Errorf(
			"listing environments: %w", err,
		)
	}

	var result []ResourceInfo
	for i := range list.Items {
		result = append(
			result,
			resourceInfoFromUnstructured(
				"Environment", &list.Items[i],
			),
		)
	}
	return result, nil
}

// getEnvironmentTopology builds the resource DAG for an
// environment.
func (s *Server) getEnvironmentTopology(
	ctx context.Context,
	envName string,
) (*TopologyNode, error) {
	gvr := cleanroomGVRs[0].Resource
	obj, err := s.dynClient.Resource(gvr).
		Namespace(s.namespace).
		Get(ctx, envName, metav1.GetOptions{})
	if err != nil {
		return nil, fmt.Errorf(
			"getting environment %s: %w", envName, err,
		)
	}

	envInfo := resourceInfoFromUnstructured(
		"Environment", obj,
	)
	root := TopologyNode{Resource: envInfo}

	// Find children by owner label.
	labelSelector := fmt.Sprintf(
		"cleanroom.azure.com/environment=%s", envName,
	)

	for _, gvrDef := range cleanroomGVRs[1:] {
		children, err := s.dynClient.
			Resource(gvrDef.Resource).
			Namespace(s.namespace).
			List(ctx, metav1.ListOptions{
				LabelSelector: labelSelector,
			})
		if err != nil {
			continue
		}
		for i := range children.Items {
			child := resourceInfoFromUnstructured(
				gvrDef.Kind, &children.Items[i],
			)
			root.Children = append(
				root.Children,
				TopologyNode{Resource: child},
			)
		}
	}

	// Also find ModelRegistrations that reference this env.
	mrList, err := s.dynClient.
		Resource(cleanroomGVRs[7].Resource).
		Namespace(s.namespace).
		List(ctx, metav1.ListOptions{})
	if err == nil {
		for i := range mrList.Items {
			envRef, _, _ := unstructured.NestedString(
				mrList.Items[i].Object,
				"spec", "environmentRef",
			)
			if envRef == envName {
				mrInfo := resourceInfoFromUnstructured(
					"ModelRegistration", &mrList.Items[i],
				)
				mrNode := TopologyNode{Resource: mrInfo}

				// Find ModelDeployments for this ModelRegistration.
				mdList, err := s.dynClient.
					Resource(cleanroomGVRs[8].Resource).
					Namespace(s.namespace).
					List(ctx, metav1.ListOptions{})
				if err == nil {
					for j := range mdList.Items {
						mrRef, _, _ :=
							unstructured.NestedString(
								mdList.Items[j].Object,
								"spec",
								"modelRegistrationRef",
							)
						if mrRef ==
							mrList.Items[i].GetName() {
							mdInfo :=
								resourceInfoFromUnstructured(
									"ModelDeployment",
									&mdList.Items[j],
								)
							mrNode.Children = append(
								mrNode.Children,
								TopologyNode{
									Resource: mdInfo,
								},
							)
						}
					}
				}

				root.Children = append(
					root.Children, mrNode,
				)
			}
		}
	}

	return &root, nil
}

// getEnvironmentEvents returns K8s events related to an
// environment and its children.
func (s *Server) getEnvironmentEvents(
	ctx context.Context,
	envName string,
) ([]EventInfo, error) {
	// Get events for all resources with the env label.
	events, err := s.clientset.CoreV1().Events(
		s.namespace,
	).List(ctx, metav1.ListOptions{})
	if err != nil {
		return nil, fmt.Errorf(
			"listing events: %w", err,
		)
	}

	// Filter to events involving this environment or
	// its children.
	var result []EventInfo
	for i := range events.Items {
		e := &events.Items[i]
		if !isEnvironmentEvent(e, envName) {
			continue
		}
		age := e.LastTimestamp.Time
		if age.IsZero() {
			age = e.EventTime.Time
		}
		result = append(result, EventInfo{
			Type:    e.Type,
			Reason:  e.Reason,
			Message: e.Message,
			Object: fmt.Sprintf(
				"%s/%s",
				e.InvolvedObject.Kind,
				e.InvolvedObject.Name,
			),
			Age:       age,
			Count:     e.Count,
			Component: e.Source.Component,
		})
	}

	sort.Slice(result, func(i, j int) bool {
		return result[i].Age.After(result[j].Age)
	})

	return result, nil
}

func isEnvironmentEvent(
	e *corev1.Event,
	envName string,
) bool {
	name := e.InvolvedObject.Name
	if name == envName {
		return true
	}
	// Children follow the pattern: envName-suffix.
	prefix := envName + "-"
	if len(name) > len(prefix) &&
		name[:len(prefix)] == prefix {
		return true
	}
	return false
}

func resourceInfoFromUnstructured(
	kind string,
	obj *unstructured.Unstructured,
) ResourceInfo {
	phase, _, _ := unstructured.NestedString(
		obj.Object, "status", "phase",
	)
	if phase == "" {
		phase = "Pending"
	}
	message, _, _ := unstructured.NestedString(
		obj.Object, "status", "message",
	)

	conditions := extractConditions(obj)

	ownerEnv := obj.GetLabels()["cleanroom.azure.com/environment"]

	infraType, _, _ := unstructured.NestedString(
		obj.Object, "spec", "infraType",
	)

	return ResourceInfo{
		Kind:       kind,
		Name:       obj.GetName(),
		Namespace:  obj.GetNamespace(),
		Phase:      phase,
		Message:    message,
		Conditions: conditions,
		Age:        obj.GetCreationTimestamp().Time,
		OwnerEnv:   ownerEnv,
		InfraType:  infraType,
		RawObject:  obj.Object,
	}
}

func extractConditions(
	obj *unstructured.Unstructured,
) []ConditionInfo {
	condSlice, found, _ := unstructured.NestedSlice(
		obj.Object, "status", "conditions",
	)
	if !found {
		return nil
	}

	var result []ConditionInfo
	for _, c := range condSlice {
		condMap, ok := c.(map[string]interface{})
		if !ok {
			continue
		}
		typ, _ := condMap["type"].(string)
		status, _ := condMap["status"].(string)
		reason, _ := condMap["reason"].(string)
		msg, _ := condMap["message"].(string)

		var lastTransition time.Time
		if lt, ok := condMap["lastTransitionTime"].(string); ok {
			lastTransition, _ = time.Parse(
				time.RFC3339, lt,
			)
		}

		result = append(result, ConditionInfo{
			Type:               typ,
			Status:             status,
			Reason:             reason,
			Message:            msg,
			LastTransitionTime: lastTransition,
		})
	}
	return result
}
