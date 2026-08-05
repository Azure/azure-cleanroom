package cloudprovider

import (
	"time"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/api/resource"

	karpv1 "sigs.k8s.io/karpenter/pkg/apis/v1"
	"sigs.k8s.io/karpenter/pkg/cloudprovider"
	"sigs.k8s.io/karpenter/pkg/scheduling"
)

// ConstructInstanceTypes returns the static list of
// instance types supported by the accr provider. For the
// POC, a single "kind-flex-standard" type is returned.
func ConstructInstanceTypes() []*cloudprovider.InstanceType {
	return []*cloudprovider.InstanceType{
		kindFlexStandard(),
	}
}

func kindFlexStandard() *cloudprovider.InstanceType {
	return &cloudprovider.InstanceType{
		Name: "kind-flex-standard",
		Requirements: scheduling.NewRequirements(
			scheduling.NewRequirement(
				corev1.LabelInstanceTypeStable,
				corev1.NodeSelectorOpIn,
				"kind-flex-standard",
			),
			scheduling.NewRequirement(
				corev1.LabelArchStable,
				corev1.NodeSelectorOpIn,
				"amd64",
			),
			scheduling.NewRequirement(
				corev1.LabelOSStable,
				corev1.NodeSelectorOpIn,
				"linux",
			),
			scheduling.NewRequirement(
				karpv1.CapacityTypeLabelKey,
				corev1.NodeSelectorOpIn,
				karpv1.CapacityTypeOnDemand,
			),
			scheduling.NewRequirement(
				corev1.LabelTopologyZone,
				corev1.NodeSelectorOpIn,
				"local",
			),
		),
		Offerings: cloudprovider.Offerings{
			&cloudprovider.Offering{
				Requirements: scheduling.NewRequirements(
					scheduling.NewRequirement(
						karpv1.CapacityTypeLabelKey,
						corev1.NodeSelectorOpIn,
						karpv1.CapacityTypeOnDemand,
					),
					scheduling.NewRequirement(
						corev1.LabelTopologyZone,
						corev1.NodeSelectorOpIn,
						"local",
					),
				),
				Price:     0.0,
				Available: true,
			},
		},
		Capacity: corev1.ResourceList{
			corev1.ResourceCPU:    resource.MustParse("4"),
			corev1.ResourceMemory: resource.MustParse("8Gi"),
			corev1.ResourcePods:   resource.MustParse("110"),
		},
		Overhead: &cloudprovider.InstanceTypeOverhead{
			KubeReserved: corev1.ResourceList{
				corev1.ResourceCPU:    resource.MustParse("100m"),
				corev1.ResourceMemory: resource.MustParse("256Mi"),
			},
			SystemReserved: corev1.ResourceList{
				corev1.ResourceCPU:    resource.MustParse("100m"),
				corev1.ResourceMemory: resource.MustParse("256Mi"),
			},
			EvictionThreshold: corev1.ResourceList{
				corev1.ResourceMemory: resource.MustParse("100Mi"),
			},
		},
	}
}

// DefaultConsolidateAfter is the default disruption
// consolidation delay for POC testing.
var DefaultConsolidateAfter = 30 * time.Second
