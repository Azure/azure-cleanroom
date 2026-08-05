package admission

// ContainerPolicyEntry represents a single container's policy.
type ContainerPolicyEntry struct {
	Name       string                    `json:"name"`
	Properties ContainerPolicyProperties `json:"properties"`
}

// ContainerPolicyProperties defines the allowed properties for a container.
type ContainerPolicyProperties struct {
	Image                string              `json:"image"`
	Command              []string            `json:"command"`
	Args                 []string            `json:"args"`
	EnvironmentVariables []PolicyEnvVar      `json:"environmentVariables"`
	VolumeMounts         []PolicyVolumeMount `json:"volumeMounts"`
	Privileged           bool                `json:"privileged"`
}

// PolicyEnvVar represents an environment variable rule in a policy.
type PolicyEnvVar struct {
	Name  string `json:"name"`
	Value string `json:"value"`
	Regex bool   `json:"regex"`
}

// PolicyVolumeMount represents a volume mount rule in a policy.
type PolicyVolumeMount struct {
	Name      string `json:"name"`
	MountPath string `json:"mountPath"`
	ReadOnly  bool   `json:"readOnly"`
}
