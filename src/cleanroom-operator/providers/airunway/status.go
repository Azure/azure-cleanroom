package airunway

import (
	cleanroomv1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	airunwayv1alpha1 "github.com/kaito-project/airunway/controller/api/v1alpha1"
)

// TranslateStatus maps cleanroom ModelDeployment status
// to AIRunway ModelDeployment status fields.
func TranslateStatus(
	clMD *cleanroomv1alpha1.ModelDeployment,
) (
	phase airunwayv1alpha1.DeploymentPhase,
	message string,
	endpoint *airunwayv1alpha1.EndpointStatus,
) {
	switch clMD.Status.Phase {
	case cleanroomv1alpha1.ModelDeploymentPhaseReady:
		phase = airunwayv1alpha1.DeploymentPhaseRunning
	case cleanroomv1alpha1.ModelDeploymentPhaseDeploying:
		phase = airunwayv1alpha1.DeploymentPhaseDeploying
	case cleanroomv1alpha1.ModelDeploymentPhaseFailed:
		phase = airunwayv1alpha1.DeploymentPhaseFailed
		message = clMD.Status.Message
	case cleanroomv1alpha1.ModelDeploymentPhasePending:
		phase = airunwayv1alpha1.DeploymentPhasePending
	default:
		phase = airunwayv1alpha1.DeploymentPhasePending
	}

	if clMD.Status.ServiceEndpoint != "" {
		endpoint = &airunwayv1alpha1.EndpointStatus{
			Service: clMD.Status.ServiceEndpoint,
			Port:    443,
		}
	}

	return phase, message, endpoint
}
