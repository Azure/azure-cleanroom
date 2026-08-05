package main

import (
	"os"

	"sigs.k8s.io/controller-runtime/pkg/log"

	v1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
	flexnodeclaimctrl "github.com/Azure/azure-cleanroom/cleanroom-operator/flexnodeclaim-controller"
	accr "github.com/Azure/azure-cleanroom/karpenter-provider-accr/cloudprovider"
	"sigs.k8s.io/karpenter/pkg/cloudprovider/overlay"
	"sigs.k8s.io/karpenter/pkg/controllers"
	"sigs.k8s.io/karpenter/pkg/controllers/state"
	"sigs.k8s.io/karpenter/pkg/operator"
)

func main() {
	ctx, op := operator.NewOperator()

	// Register the operator's FlexNodeClass and FlexNodeClaim
	// types so the provider can read/write them.
	if err := v1alpha1.AddToScheme(
		op.GetScheme(),
	); err != nil {
		log.FromContext(ctx).Error(
			err, "failed registering cleanroom scheme",
		)
	}

	instanceTypes := accr.ConstructInstanceTypes()

	cp := accr.New(ctx, op.GetClient(), instanceTypes)
	overlayCP := overlay.Decorate(
		cp, op.GetClient(), op.InstanceTypeStore,
	)
	clusterState := state.NewCluster(
		op.Clock, op.GetClient(), overlayCP,
	)

	// Register the FlexNodeClaim reconciler in this binary.
	clusterProviderEndpoint := os.Getenv(
		"CLUSTER_PROVIDER_ENDPOINT",
	)
	if clusterProviderEndpoint == "" {
		clusterProviderEndpoint =
			"http://cluster-provider-client.default.svc.cluster.local"
	}
	clusterClient := client.NewClusterClient(
		clusterProviderEndpoint,
	)

	if err := (&flexnodeclaimctrl.FlexNodeClaimReconciler{
		Client:        op.GetClient(),
		Scheme:        op.GetScheme(),
		ClusterClient: clusterClient,
		Recorder: op.Manager.GetEventRecorderFor(
			"flexnodeclaim-controller",
		),
	}).SetupWithManager(op.Manager); err != nil {
		log.FromContext(ctx).Error(
			err,
			"unable to create FlexNodeClaim controller",
		)
		os.Exit(1)
	}

	op.
		WithControllers(ctx,
			controllers.NewControllers(
				ctx,
				op.Manager,
				op.Clock,
				op.GetClient(),
				op.EventRecorder,
				overlayCP,
				cp,
				clusterState,
				op.InstanceTypeStore,
			)...).
		Start(ctx)
}
