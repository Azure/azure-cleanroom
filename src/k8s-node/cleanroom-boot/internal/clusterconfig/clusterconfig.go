// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Package clusterconfig resolves the AKS cluster's API server URL and CA
// certificate (and the managed-identity/tenant used to reach them) from the
// flex-node config envelope plus an ARM listClusterAdminCredentials call.
//
// Replicates the logic of AKSFlexNode's clusterConfigEnricher:
// pkg/bootstrapper/cluster_config_enricher.go
package clusterconfig

import (
	"context"
	"encoding/base64"
	"fmt"
	"strings"

	"github.com/Azure/azure-sdk-for-go/sdk/azidentity"
	"github.com/Azure/azure-sdk-for-go/sdk/resourcemanager/containerservice/armcontainerservice/v6"
	log "github.com/sirupsen/logrus"
	"sigs.k8s.io/yaml"

	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/config"
)

// ClusterDetails carries everything the api-server-proxy needs to build its
// upstream kubeconfig: the real API server endpoint + CA, and the managed
// identity/tenant the proxy authenticates its own requests with.
type ClusterDetails struct {
	ApiServerURL    string
	ApiServerCACert []byte
	MsiClientID     string
	TenantID        string
}

// Resolve reads the managed identity and target cluster from the flex-node
// config envelope, calls ARM ListClusterAdminCredentials, and parses the
// returned kubeconfig for the API server URL and CA certificate.
func Resolve(cfg *config.CleanroomConfig) (ClusterDetails, error) {
	flexCfg := cfg.FlexNodeConfig

	azure, _ := flexCfg["azure"].(map[string]any)
	if azure == nil {
		return ClusterDetails{}, fmt.Errorf("flexNodeConfig.azure not found")
	}

	targetCluster, _ := azure["targetCluster"].(map[string]any)
	if targetCluster == nil {
		return ClusterDetails{}, fmt.Errorf("flexNodeConfig.azure.targetCluster not found")
	}
	resourceID, _ := targetCluster["resourceId"].(string)
	if resourceID == "" {
		return ClusterDetails{}, fmt.Errorf("flexNodeConfig.azure.targetCluster.resourceId is empty")
	}

	mi, _ := azure["managedIdentity"].(map[string]any)
	if mi == nil {
		return ClusterDetails{}, fmt.Errorf("flexNodeConfig.azure.managedIdentity not found")
	}
	clientID, _ := mi["clientId"].(string)
	if clientID == "" {
		return ClusterDetails{}, fmt.Errorf("flexNodeConfig.azure.managedIdentity.clientId is empty")
	}

	tenantID, _ := azure["tenantId"].(string)
	if tenantID == "" {
		return ClusterDetails{}, fmt.Errorf("flexNodeConfig.azure.tenantId is empty")
	}

	log.Infof("Resolving cluster config for: %s", resourceID)

	subscriptionID, clusterRG, clusterName, err := parseAKSResourceID(resourceID)
	if err != nil {
		return ClusterDetails{}, err
	}

	// Create MSI credential (same approach as AKSFlexNode's auth.msiCredential).
	cred, err := azidentity.NewManagedIdentityCredential(
		&azidentity.ManagedIdentityCredentialOptions{
			ID: azidentity.ClientID(clientID),
		},
	)
	if err != nil {
		return ClusterDetails{}, fmt.Errorf("creating managed identity credential: %w", err)
	}

	mcClient, err := armcontainerservice.NewManagedClustersClient(subscriptionID, cred, nil)
	if err != nil {
		return ClusterDetails{}, fmt.Errorf("creating managed clusters client: %w", err)
	}

	resp, err := mcClient.ListClusterAdminCredentials(
		context.Background(), clusterRG, clusterName, nil)
	if err != nil {
		return ClusterDetails{}, fmt.Errorf(
			"listing cluster admin credentials for %s/%s: %w", clusterRG, clusterName, err)
	}

	if len(resp.Kubeconfigs) == 0 {
		return ClusterDetails{}, fmt.Errorf("no kubeconfig returned in cluster admin credentials response")
	}

	kubeconfig := resp.Kubeconfigs[0]
	if kubeconfig == nil || len(kubeconfig.Value) == 0 {
		return ClusterDetails{}, fmt.Errorf("kubeconfig value is empty in cluster admin credentials response")
	}

	serverURL, caCertData, err := extractClusterInfoFromKubeconfig(kubeconfig.Value)
	if err != nil {
		return ClusterDetails{}, fmt.Errorf("extracting cluster info from kubeconfig: %w", err)
	}

	caCert, err := base64.StdEncoding.DecodeString(caCertData)
	if err != nil {
		return ClusterDetails{}, fmt.Errorf("decoding CA cert: %w", err)
	}

	log.Infof("API server: %s", serverURL)
	return ClusterDetails{
		ApiServerURL:    serverURL,
		ApiServerCACert: caCert,
		MsiClientID:     clientID,
		TenantID:        tenantID,
	}, nil
}

// minimalKubeconfig holds just the fields we need from an admin kubeconfig.
// Uses json tags because sigs.k8s.io/yaml converts YAML to JSON first.
type minimalKubeconfig struct {
	Clusters []struct {
		Cluster struct {
			Server                   string `json:"server"`
			CertificateAuthorityData string `json:"certificate-authority-data"`
		} `json:"cluster"`
	} `json:"clusters"`
}

// extractClusterInfoFromKubeconfig parses a kubeconfig YAML and returns the
// server URL and base64-encoded CA certificate data. Replicates AKSFlexNode's
// extractClusterInfoFromKubeconfig in pkg/bootstrapper/cluster_config_enricher.go.
func extractClusterInfoFromKubeconfig(data []byte) (serverURL, caCertData string, err error) {
	var kc minimalKubeconfig
	if err := yaml.Unmarshal(data, &kc); err != nil {
		return "", "", fmt.Errorf("failed to parse kubeconfig YAML: %w", err)
	}
	if len(kc.Clusters) == 0 {
		return "", "", fmt.Errorf("no clusters found in kubeconfig")
	}
	cluster := kc.Clusters[0].Cluster
	if cluster.Server == "" {
		return "", "", fmt.Errorf("server URL is empty in kubeconfig")
	}
	return cluster.Server, cluster.CertificateAuthorityData, nil
}

// parseAKSResourceID extracts subscription, resource group, and cluster name
// from a fully-qualified AKS resource ID.
func parseAKSResourceID(resourceID string) (subscriptionID, resourceGroup, clusterName string, err error) {
	// Format: /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ContainerService/managedClusters/{name}
	parts := strings.Split(strings.TrimPrefix(resourceID, "/"), "/")
	if len(parts) < 8 {
		return "", "", "", fmt.Errorf("invalid AKS resource ID: %s", resourceID)
	}

	for i, part := range parts {
		switch strings.ToLower(part) {
		case "subscriptions":
			if i+1 < len(parts) {
				subscriptionID = parts[i+1]
			}
		case "resourcegroups":
			if i+1 < len(parts) {
				resourceGroup = parts[i+1]
			}
		case "managedclusters":
			if i+1 < len(parts) {
				clusterName = parts[i+1]
			}
		}
	}

	if subscriptionID == "" || resourceGroup == "" || clusterName == "" {
		return "", "", "", fmt.Errorf("could not parse AKS resource ID: %s", resourceID)
	}
	return subscriptionID, resourceGroup, clusterName, nil
}
