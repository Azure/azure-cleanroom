package cloudprovider

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/awslabs/operatorpkg/status"
	"github.com/samber/lo"
	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/types"
	"sigs.k8s.io/controller-runtime/pkg/client"
	"sigs.k8s.io/controller-runtime/pkg/log"

	v1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	karpv1 "sigs.k8s.io/karpenter/pkg/apis/v1"
	"sigs.k8s.io/karpenter/pkg/cloudprovider"
	"sigs.k8s.io/karpenter/pkg/scheduling"
)

const (
	providerPrefix = "accr://"
)

// CloudProvider implements Karpenter's CloudProvider
// interface for the accr provider. It creates
// FlexNodeClaim CRs as the bridge to the cleanroom
// operator.
type CloudProvider struct {
	kubeClient    client.Client
	instanceTypes []*cloudprovider.InstanceType
}

// New creates a new accr CloudProvider.
func New(
	_ context.Context,
	kubeClient client.Client,
	instanceTypes []*cloudprovider.InstanceType,
) *CloudProvider {
	return &CloudProvider{
		kubeClient:    kubeClient,
		instanceTypes: instanceTypes,
	}
}

func (c *CloudProvider) Name() string {
	return "accr"
}

func (c *CloudProvider) GetSupportedNodeClasses() []status.Object {
	return []status.Object{&v1alpha1.FlexNodeClass{}}
}

func (c *CloudProvider) RepairPolicies() []cloudprovider.RepairPolicy {
	return []cloudprovider.RepairPolicy{
		{
			ConditionType:      corev1.NodeReady,
			ConditionStatus:    corev1.ConditionFalse,
			TolerationDuration: 10 * time.Minute,
		},
		{
			ConditionType:      corev1.NodeReady,
			ConditionStatus:    corev1.ConditionUnknown,
			TolerationDuration: 10 * time.Minute,
		},
	}
}

func (c *CloudProvider) IsDrifted(
	_ context.Context,
	_ *karpv1.NodeClaim,
) (cloudprovider.DriftReason, error) {
	return "", nil
}

func (c *CloudProvider) GetInstanceTypes(
	_ context.Context,
	_ *karpv1.NodePool,
) ([]*cloudprovider.InstanceType, error) {
	return c.instanceTypes, nil
}

// Create resolves the FlexNodeClass, picks an instance
// type, creates a FlexNodeClaim CR, and returns the
// hydrated NodeClaim with a providerID.
func (c *CloudProvider) Create(
	ctx context.Context,
	nodeClaim *karpv1.NodeClaim,
) (*karpv1.NodeClaim, error) {
	logger := log.FromContext(ctx)

	// Resolve FlexNodeClass.
	nodeClass, err := c.resolveNodeClass(
		ctx, nodeClaim,
	)
	if err != nil {
		return nil, err
	}

	clusterName := nodeClass.Spec.ClusterName
	providerID := fmt.Sprintf(
		"%s%s/%s", providerPrefix, clusterName, nodeClaim.Name,
	)

	// Pick instance type from requirements.
	instanceType, err := c.pickInstanceType(nodeClaim)
	if err != nil {
		return nil, cloudprovider.NewInsufficientCapacityError(
			fmt.Errorf("picking instance type: %w", err),
		)
	}

	logger.Info("Creating FlexNodeClaim",
		"name", nodeClaim.Name,
		"nodeClass", nodeClass.Name,
		"cluster", clusterName,
		"instanceType", instanceType.Name,
		"providerID", providerID)

	// Create FlexNodeClaim CR.
	fnc := &v1alpha1.FlexNodeClaim{
		ObjectMeta: metav1.ObjectMeta{
			Name:      nodeClaim.Name,
			Namespace: "default",
			Labels: map[string]string{
				"cleanroom.azure.com/nodeclass": nodeClass.Name,
				"cleanroom.azure.com/nodeclaim": nodeClaim.Name,
			},
		},
		Spec: v1alpha1.FlexNodeClaimSpec{
			NodeClassName: nodeClass.Name,
			NodeClaimName: nodeClaim.Name,
			InstanceType:  instanceType.Name,
		},
	}

	if err := c.kubeClient.Create(ctx, fnc); err != nil {
		if !apierrors.IsAlreadyExists(err) {
			return nil, fmt.Errorf(
				"creating FlexNodeClaim: %w", err,
			)
		}
		logger.Info(
			"FlexNodeClaim already exists, idempotent",
			"name", nodeClaim.Name,
		)
	}

	// Return hydrated NodeClaim.
	nc := nodeClaim.DeepCopy()
	nc.Status.ProviderID = providerID
	nc.Status.Capacity = instanceType.Capacity
	nc.Status.Allocatable = instanceType.Allocatable()
	nc.Labels = resolveLabels(
		nc.Labels, instanceType, nodeClaim,
	)

	return nc, nil
}

// Delete removes the FlexNodeClaim CR. Karpenter handles
// draining; we just delete infrastructure.
func (c *CloudProvider) Delete(
	ctx context.Context,
	nodeClaim *karpv1.NodeClaim,
) error {
	logger := log.FromContext(ctx)

	_, fncName, err := parseProviderID(
		nodeClaim.Status.ProviderID,
	)
	if err != nil {
		return cloudprovider.NewNodeClaimNotFoundError(err)
	}

	ns := "default"

	fnc := &v1alpha1.FlexNodeClaim{}
	if err := c.kubeClient.Get(ctx, types.NamespacedName{
		Name:      fncName,
		Namespace: ns,
	}, fnc); err != nil {
		if apierrors.IsNotFound(err) {
			return cloudprovider.NewNodeClaimNotFoundError(
				fmt.Errorf(
					"FlexNodeClaim %s/%s not found",
					ns, fncName,
				),
			)
		}
		return fmt.Errorf(
			"getting FlexNodeClaim %s/%s: %w",
			ns, fncName, err,
		)
	}

	// If already deleting with a finalizer, return nil
	// (deletion in progress).
	if !fnc.DeletionTimestamp.IsZero() {
		return nil
	}

	logger.Info("Deleting FlexNodeClaim",
		"name", fncName,
		"namespace", ns,
		"nodeClass", fnc.Spec.NodeClassName)

	if err := c.kubeClient.Delete(ctx, fnc); err != nil {
		if apierrors.IsNotFound(err) {
			return cloudprovider.NewNodeClaimNotFoundError(
				fmt.Errorf(
					"FlexNodeClaim %s/%s not found",
					ns, fncName,
				),
			)
		}
		return fmt.Errorf(
			"deleting FlexNodeClaim %s/%s: %w",
			ns, fncName, err,
		)
	}

	return nil
}

// Get retrieves a NodeClaim by its providerID.
func (c *CloudProvider) Get(
	ctx context.Context,
	providerID string,
) (*karpv1.NodeClaim, error) {
	_, fncName, err := parseProviderID(providerID)
	if err != nil {
		return nil, cloudprovider.NewNodeClaimNotFoundError(err)
	}

	// We need to search across namespaces for the
	// FlexNodeClaim. List with label filter.
	var fncList v1alpha1.FlexNodeClaimList
	if err := c.kubeClient.List(ctx, &fncList,
		client.MatchingLabels{
			"cleanroom.azure.com/nodeclaim": fncName,
		},
	); err != nil {
		return nil, fmt.Errorf(
			"listing FlexNodeClaims: %w", err,
		)
	}

	if len(fncList.Items) == 0 {
		return nil, cloudprovider.NewNodeClaimNotFoundError(
			fmt.Errorf(
				"FlexNodeClaim %s not found", fncName,
			),
		)
	}

	fnc := &fncList.Items[0]
	return flexNodeClaimToNodeClaim(fnc, providerID), nil
}

// List returns all NodeClaims managed by this provider.
func (c *CloudProvider) List(
	ctx context.Context,
) ([]*karpv1.NodeClaim, error) {
	var fncList v1alpha1.FlexNodeClaimList
	if err := c.kubeClient.List(
		ctx, &fncList,
	); err != nil {
		return nil, fmt.Errorf(
			"listing FlexNodeClaims: %w", err,
		)
	}

	var nodeClaims []*karpv1.NodeClaim
	for i := range fncList.Items {
		fnc := &fncList.Items[i]
		// Skip FlexNodeClaims being deleted.
		if !fnc.DeletionTimestamp.IsZero() {
			continue
		}

		// Resolve the cluster name from NodeClass.
		nodeClassName := fnc.Spec.NodeClassName
		var nodeClass v1alpha1.FlexNodeClass
		ncErr := c.kubeClient.Get(ctx, types.NamespacedName{
			Name: nodeClassName,
		}, &nodeClass)
		clusterID := nodeClassName
		if ncErr == nil {
			clusterID = nodeClass.Spec.ClusterName
		}

		providerID := fmt.Sprintf(
			"%s%s/%s",
			providerPrefix,
			clusterID,
			fnc.Name,
		)
		nodeClaims = append(
			nodeClaims,
			flexNodeClaimToNodeClaim(fnc, providerID),
		)
	}

	return nodeClaims, nil
}

// resolveNodeClass fetches the FlexNodeClass referenced
// by the NodeClaim.
func (c *CloudProvider) resolveNodeClass(
	ctx context.Context,
	nodeClaim *karpv1.NodeClaim,
) (*v1alpha1.FlexNodeClass, error) {
	nodeClass := &v1alpha1.FlexNodeClass{}
	if err := c.kubeClient.Get(
		ctx,
		types.NamespacedName{
			Name: nodeClaim.Spec.NodeClassRef.Name,
		},
		nodeClass,
	); err != nil {
		if apierrors.IsNotFound(err) {
			return nil, cloudprovider.NewInsufficientCapacityError(
				fmt.Errorf(
					"FlexNodeClass %s not found: %w",
					nodeClaim.Spec.NodeClassRef.Name,
					err,
				),
			)
		}
		return nil, fmt.Errorf(
			"getting FlexNodeClass %s: %w",
			nodeClaim.Spec.NodeClassRef.Name, err,
		)
	}

	// Check Ready condition.
	readyCond := nodeClass.StatusConditions().Get(
		status.ConditionReady,
	)
	if readyCond != nil && readyCond.IsFalse() {
		return nil, cloudprovider.NewNodeClassNotReadyError(
			fmt.Errorf(readyCond.Message),
		)
	}

	return nodeClass, nil
}

// pickInstanceType selects an instance type that matches
// the NodeClaim requirements.
func (c *CloudProvider) pickInstanceType(
	nodeClaim *karpv1.NodeClaim,
) (*cloudprovider.InstanceType, error) {
	requirements := scheduling.NewNodeSelectorRequirementsWithMinValues(
		nodeClaim.Spec.Requirements...,
	)

	// Find instance types with available compatible
	// offerings.
	compatible := lo.Filter(
		c.instanceTypes,
		func(
			it *cloudprovider.InstanceType, _ int,
		) bool {
			return it.Offerings.Available().HasCompatible(
				requirements,
			)
		},
	)

	if len(compatible) == 0 {
		return nil, fmt.Errorf(
			"no compatible instance types found",
		)
	}

	// Return cheapest compatible.
	return compatible[0], nil
}

// parseProviderID extracts cluster name and FlexNodeClaim
// name from accr://<clusterName>/<name>.
func parseProviderID(
	providerID string,
) (clusterName string, fncName string, err error) {
	if !strings.HasPrefix(providerID, providerPrefix) {
		return "", "", fmt.Errorf(
			"invalid providerID %q: missing prefix %s",
			providerID, providerPrefix,
		)
	}

	rest := strings.TrimPrefix(providerID, providerPrefix)
	parts := strings.SplitN(rest, "/", 2)
	if len(parts) != 2 || parts[0] == "" || parts[1] == "" {
		return "", "", fmt.Errorf(
			"invalid providerID %q: "+
				"expected accr://<cluster>/<name>",
			providerID,
		)
	}

	return parts[0], parts[1], nil
}

// flexNodeClaimToNodeClaim converts a FlexNodeClaim to a
// Karpenter NodeClaim.
func flexNodeClaimToNodeClaim(
	fnc *v1alpha1.FlexNodeClaim,
	providerID string,
) *karpv1.NodeClaim {
	return &karpv1.NodeClaim{
		ObjectMeta: metav1.ObjectMeta{
			Name:   fnc.Name,
			Labels: fnc.Labels,
		},
		Status: karpv1.NodeClaimStatus{
			ProviderID: providerID,
			NodeName:   fnc.Status.NodeName,
		},
	}
}

// resolveLabels merges instance type labels into the
// NodeClaim labels.
func resolveLabels(
	labels map[string]string,
	instanceType *cloudprovider.InstanceType,
	nodeClaim *karpv1.NodeClaim,
) map[string]string {
	ret := make(map[string]string, len(labels))
	for k, v := range labels {
		ret[k] = v
	}

	// Resolve single-value requirements from the
	// NodeClaim spec.
	for _, r := range nodeClaim.Spec.Requirements {
		if len(r.Values) == 1 &&
			r.Operator == corev1.NodeSelectorOpIn {
			ret[r.Key] = r.Values[0]
		}
	}

	// Add instance type label.
	ret[corev1.LabelInstanceTypeStable] = instanceType.Name

	// Add single-value requirements from the instance
	// type.
	for _, r := range instanceType.Requirements {
		if r.Len() == 1 &&
			r.Operator() == corev1.NodeSelectorOpIn {
			ret[r.Key] = r.Values()[0]
		}
	}

	return ret
}
