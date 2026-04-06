package admission

import (
	"encoding/json"
	"log"
	"os"
)

// LoggingController is an admission controller that logs all pods and allows them
type LoggingController struct {
	logger *log.Logger
}

// NewLoggingController creates a new logging admission controller
func NewLoggingController() *LoggingController {
	return &LoggingController{
		logger: log.New(os.Stdout, "[admission-logging] ", log.LstdFlags|log.Lmicroseconds),
	}
}

// Name returns the name of the controller
func (c *LoggingController) Name() string {
	return "logging"
}

// Admit logs the pod details and allows it
func (c *LoggingController) Admit(req *Request) *Decision {
	c.logger.Printf("=== Pod Admission Request ===")
	c.logger.Printf("Namespace: %s", req.Namespace)
	c.logger.Printf("Name: %s", req.Name)

	// Log metadata
	if metadata, ok := req.Pod["metadata"].(map[string]interface{}); ok {
		if labels, ok := metadata["labels"].(map[string]interface{}); ok && len(labels) > 0 {
			labelsJSON, _ := json.Marshal(labels)
			c.logger.Printf("Labels: %s", string(labelsJSON))
		}
		if annotations, ok := metadata["annotations"].(map[string]interface{}); ok && len(annotations) > 0 {
			annotationsJSON, _ := json.Marshal(annotations)
			c.logger.Printf("Annotations: %s", string(annotationsJSON))
		}
	}

	// Log spec details
	if spec, ok := req.Pod["spec"].(map[string]interface{}); ok {
		// Log containers
		if containers, ok := spec["containers"].([]interface{}); ok {
			c.logger.Printf("Containers: %d", len(containers))
			for i, cont := range containers {
				if container, ok := cont.(map[string]interface{}); ok {
					name, _ := container["name"].(string)
					image, _ := container["image"].(string)
					c.logger.Printf("  [%d] name=%s image=%s", i, name, image)
				}
			}
		}

		// Log init containers
		if initContainers, ok := spec["initContainers"].([]interface{}); ok && len(initContainers) > 0 {
			c.logger.Printf("Init Containers: %d", len(initContainers))
			for i, cont := range initContainers {
				if container, ok := cont.(map[string]interface{}); ok {
					name, _ := container["name"].(string)
					image, _ := container["image"].(string)
					c.logger.Printf("  [%d] name=%s image=%s", i, name, image)
				}
			}
		}

		// Log security-relevant settings
		if hostNetwork, ok := spec["hostNetwork"].(bool); ok && hostNetwork {
			c.logger.Printf("WARNING: Pod uses hostNetwork")
		}
		if hostPID, ok := spec["hostPID"].(bool); ok && hostPID {
			c.logger.Printf("WARNING: Pod uses hostPID")
		}
		if hostIPC, ok := spec["hostIPC"].(bool); ok && hostIPC {
			c.logger.Printf("WARNING: Pod uses hostIPC")
		}

		// Check for privileged containers
		c.logSecurityContext(spec)

		// Log volumes
		if volumes, ok := spec["volumes"].([]interface{}); ok && len(volumes) > 0 {
			c.logger.Printf("Volumes: %d", len(volumes))
			for _, vol := range volumes {
				if volume, ok := vol.(map[string]interface{}); ok {
					name, _ := volume["name"].(string)
					volType := getVolumeType(volume)
					c.logger.Printf("  - %s (type: %s)", name, volType)
				}
			}
		}

		// Log service account
		if sa, ok := spec["serviceAccountName"].(string); ok {
			c.logger.Printf("ServiceAccount: %s", sa)
		}

		// Log node selector/affinity
		if nodeSelector, ok := spec["nodeSelector"].(map[string]interface{}); ok && len(nodeSelector) > 0 {
			nodeSelectorJSON, _ := json.Marshal(nodeSelector)
			c.logger.Printf("NodeSelector: %s", string(nodeSelectorJSON))
		}
	}

	c.logger.Printf("=== Decision: ALLOWED ===")
	return Allow("logging controller allows all pods")
}

func (c *LoggingController) logSecurityContext(spec map[string]interface{}) {
	checkContainers := func(containerType string, containers []interface{}) {
		for _, cont := range containers {
			if container, ok := cont.(map[string]interface{}); ok {
				name, _ := container["name"].(string)
				if secCtx, ok := container["securityContext"].(map[string]interface{}); ok {
					if privileged, ok := secCtx["privileged"].(bool); ok && privileged {
						c.logger.Printf("WARNING: %s container '%s' is privileged", containerType, name)
					}
					if runAsRoot, ok := secCtx["runAsUser"].(float64); ok && runAsRoot == 0 {
						c.logger.Printf("WARNING: %s container '%s' runs as root", containerType, name)
					}
					if caps, ok := secCtx["capabilities"].(map[string]interface{}); ok {
						if add, ok := caps["add"].([]interface{}); ok && len(add) > 0 {
							c.logger.Printf("WARNING: %s container '%s' adds capabilities: %v", containerType, name, add)
						}
					}
				}
			}
		}
	}

	if containers, ok := spec["containers"].([]interface{}); ok {
		checkContainers("", containers)
	}
	if initContainers, ok := spec["initContainers"].([]interface{}); ok {
		checkContainers("init", initContainers)
	}
}

func getVolumeType(volume map[string]interface{}) string {
	volumeTypes := []string{
		"hostPath", "emptyDir", "secret", "configMap", "persistentVolumeClaim",
		"downwardAPI", "projected", "csi", "nfs", "iscsi", "glusterfs",
		"rbd", "cephfs", "gitRepo", "awsElasticBlockStore", "gcePersistentDisk",
		"azureDisk", "azureFile", "vsphereVolume",
	}

	for _, vt := range volumeTypes {
		if _, ok := volume[vt]; ok {
			return vt
		}
	}
	return "unknown"
}
