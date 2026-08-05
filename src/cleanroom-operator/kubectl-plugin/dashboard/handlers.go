package dashboard

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"strings"
	"time"

	log "github.com/sirupsen/logrus"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/apis/meta/v1/unstructured"
	"k8s.io/apimachinery/pkg/types"

	"github.com/Azure/azure-cleanroom/cleanroom-operator/kubectl-plugin/prereqs"
)

func (s *Server) handleIndex(
	w http.ResponseWriter, r *http.Request,
) {
	envs, err := s.listEnvironments(r.Context())
	if err != nil {
		log.WithError(err).Error(
			"Failed to list environments",
		)
		http.Error(w,
			"Failed to list environments",
			http.StatusInternalServerError)
		return
	}

	data := map[string]interface{}{
		"Environments": envs,
		"Now":          time.Now(),
	}

	if err := s.templates.ExecuteTemplate(
		w, "index.html", data,
	); err != nil {
		log.WithError(err).Error(
			"Failed to render index",
		)
	}
}

func (s *Server) handleTopology(
	w http.ResponseWriter, r *http.Request,
) {
	name := r.PathValue("name")
	if name == "" {
		http.Error(w, "name required",
			http.StatusBadRequest)
		return
	}

	topology, err := s.getEnvironmentTopology(
		r.Context(), name,
	)
	if err != nil {
		log.WithError(err).Error(
			"Failed to get topology",
		)
		http.Error(w, "Failed to get topology",
			http.StatusInternalServerError)
		return
	}

	if err := s.templates.ExecuteTemplate(
		w, "topology.html", topology,
	); err != nil {
		log.WithError(err).Error(
			"Failed to render topology",
		)
	}
}

func (s *Server) handleEvents(
	w http.ResponseWriter, r *http.Request,
) {
	name := r.PathValue("name")
	if name == "" {
		http.Error(w, "name required",
			http.StatusBadRequest)
		return
	}

	events, err := s.getEnvironmentEvents(
		r.Context(), name,
	)
	if err != nil {
		log.WithError(err).Error(
			"Failed to get events",
		)
		http.Error(w, "Failed to get events",
			http.StatusInternalServerError)
		return
	}

	if err := s.templates.ExecuteTemplate(
		w, "events.html", events,
	); err != nil {
		log.WithError(err).Error(
			"Failed to render events",
		)
	}
}

func (s *Server) handleConditions(
	w http.ResponseWriter, r *http.Request,
) {
	name := r.PathValue("name")
	if name == "" {
		http.Error(w, "name required",
			http.StatusBadRequest)
		return
	}

	topology, err := s.getEnvironmentTopology(
		r.Context(), name,
	)
	if err != nil {
		log.WithError(err).Error(
			"Failed to get conditions",
		)
		http.Error(w, "Failed to get conditions",
			http.StatusInternalServerError)
		return
	}

	if err := s.templates.ExecuteTemplate(
		w, "conditions.html", topology,
	); err != nil {
		log.WithError(err).Error(
			"Failed to render conditions",
		)
	}
}

func (s *Server) handleReconcile(
	w http.ResponseWriter, r *http.Request,
) {
	name := r.PathValue("name")
	if name == "" {
		http.Error(w, "name required",
			http.StatusBadRequest)
		return
	}

	gvr := cleanroomGVRs[0].Resource // environments
	patchData := fmt.Sprintf(
		`{"metadata":{"annotations":{`+
			`"reconcile.cleanroom.azure.com/requestedAt"`+
			`:"%s"}}}`,
		time.Now().UTC().Format(time.RFC3339),
	)

	_, err := s.dynClient.Resource(gvr).
		Namespace(s.namespace).
		Patch(
			r.Context(),
			name,
			types.MergePatchType,
			[]byte(patchData),
			metav1.PatchOptions{},
		)
	if err != nil {
		log.WithError(err).Error(
			"Failed to reconcile environment",
		)
		http.Error(w, "Failed to reconcile",
			http.StatusInternalServerError)
		return
	}

	w.Header().Set("HX-Trigger", "env-reconciled")
	fmt.Fprint(w,
		`<span class="flash-success">`+
			`Reconcile requested</span>`)
}

func (s *Server) handleDelete(
	w http.ResponseWriter, r *http.Request,
) {
	name := r.PathValue("name")
	if name == "" {
		http.Error(w, "name required",
			http.StatusBadRequest)
		return
	}

	gvr := cleanroomGVRs[0].Resource

	// Patch deletionPolicy before deleting so the
	// operator finalizer uses the chosen policy.
	deleteInfra := r.URL.Query().Get(
		"deleteInfra",
	)
	policy := "retain"
	if deleteInfra == "true" {
		policy = "delete"
	}
	patch := []byte(fmt.Sprintf(
		`{"spec":{"deletionPolicy":"%s"}}`,
		policy,
	))
	_, err := s.dynClient.Resource(gvr).
		Namespace(s.namespace).
		Patch(
			r.Context(),
			name,
			types.MergePatchType,
			patch,
			metav1.PatchOptions{},
		)
	if err != nil {
		log.WithError(err).Error(
			"Failed to patch deletionPolicy",
		)
		http.Error(w,
			"Failed to update deletion policy",
			http.StatusInternalServerError)
		return
	}

	err = s.dynClient.Resource(gvr).
		Namespace(s.namespace).
		Delete(
			r.Context(),
			name,
			metav1.DeleteOptions{},
		)
	if err != nil {
		log.WithError(err).Error(
			"Failed to delete environment",
		)
		http.Error(w, "Failed to delete",
			http.StatusInternalServerError)
		return
	}

	w.Header().Set("HX-Redirect", "/")
	w.WriteHeader(http.StatusOK)
}

// createRequest is the JSON body for environment
// creation.
type createRequest struct {
	Name      string `json:"name"`
	InfraType string `json:"infraType"`
	Location  string `json:"location"`
}

func (s *Server) handleCreate(
	w http.ResponseWriter, r *http.Request,
) {
	var req createRequest
	if err := json.NewDecoder(r.Body).Decode(
		&req,
	); err != nil {
		http.Error(w, "Invalid request body",
			http.StatusBadRequest)
		return
	}

	if req.Name == "" || req.InfraType == "" {
		http.Error(w,
			"name and infraType are required",
			http.StatusBadRequest)
		return
	}

	if req.InfraType != "virtual" &&
		req.InfraType != "aks" {
		http.Error(w,
			"infraType must be 'virtual' or 'aks'",
			http.StatusBadRequest)
		return
	}

	spec := map[string]interface{}{
		"infraType":   req.InfraType,
		"autoApprove": true,
		"enableCA":    true,
	}

	if req.InfraType == "aks" {
		if req.Location == "" {
			http.Error(w,
				"location is required for aks",
				http.StatusBadRequest)
			return
		}

		if err := prereqs.Run(
			r.Context(), &prereqs.Options{
				Name:      req.Name,
				Location:  req.Location,
				Namespace: s.namespace,
				Clientset: s.clientset,
				Yes:       true,
			},
		); err != nil {
			log.WithError(err).Error(
				"Failed to prepare prereqs",
			)
			errMsg := cleanAzureError(
				err.Error(),
			)
			http.Error(w, errMsg,
				http.StatusInternalServerError)
			return
		}
		spec["prereqsConfigRef"] = req.Name
	}

	// Always enable inferencing profile.
	profiles := map[string]interface{}{
		"inferencing": map[string]interface{}{
			"kserveProfile": map[string]interface{}{
				"enabled": true,
			},
		},
	}

	if req.InfraType == "virtual" {
		profiles["flexNode"] =
			map[string]interface{}{
				"enabled": true,
				"mode":    "auto",
			}
	}

	spec["profiles"] = profiles

	// Derive oidcContainerName.
	oidcName := req.Name
	if user := os.Getenv("USER"); user != "" {
		oidcName = req.Name + "-" + user
	}
	spec["governanceService"] = map[string]interface{}{
		"oidcContainerName": oidcName,
	}

	obj := &unstructured.Unstructured{
		Object: map[string]interface{}{
			"apiVersion": "cleanroom.azure.com/v1alpha1",
			"kind":       "Environment",
			"metadata": map[string]interface{}{
				"name":      req.Name,
				"namespace": s.namespace,
			},
			"spec": spec,
		},
	}

	gvr := cleanroomGVRs[0].Resource
	_, err := s.dynClient.Resource(gvr).
		Namespace(s.namespace).
		Create(r.Context(), obj, metav1.CreateOptions{})
	if err != nil {
		log.WithError(err).Error(
			"Failed to create environment",
		)
		http.Error(w,
			"Failed to create environment: "+
				err.Error(),
			http.StatusInternalServerError)
		return
	}

	w.WriteHeader(http.StatusCreated)
	fmt.Fprintf(w, "Environment %s created", req.Name)
}

// azureContextResponse is the JSON response for the
// azure-context endpoint.
type azureContextResponse struct {
	Subscription   string `json:"subscription"`
	SubId          string `json:"subId"`
	Tenant         string `json:"tenant"`
	TenantId       string `json:"tenantId"`
	ClusterRG      string `json:"clusterRG"`
	CcfRG          string `json:"ccfRG"`
	StorageAccount string `json:"storageAccount"`
	ConfigMap      string `json:"configMap"`
}

func (s *Server) handleAzureContext(
	w http.ResponseWriter, r *http.Request,
) {
	name := r.URL.Query().Get("name")

	azCtx, err := prereqs.GetAzureContext(r.Context())
	if err != nil {
		http.Error(w,
			"Not logged in to Azure CLI. "+
				"Run 'az login' first.",
			http.StatusServiceUnavailable)
		return
	}

	resp := azureContextResponse{
		Subscription: azCtx.SubscriptionName,
		SubId:        azCtx.SubscriptionId,
		Tenant:       azCtx.TenantName,
		TenantId:     azCtx.TenantId,
	}

	// Derive resource names if a name was provided.
	if name != "" {
		o := &prereqs.Options{Name: name}
		if err := prereqs.DeriveResourceGroups(
			o,
		); err == nil {
			resp.ClusterRG = o.ResourceGroup
			resp.CcfRG = o.CcfResourceGroup
			resp.StorageAccount =
				prereqs.DeriveStorageAccountName(
					o.CcfResourceGroup,
				)
		}
		resp.ConfigMap = name
	}

	w.Header().Set(
		"Content-Type", "application/json",
	)
	json.NewEncoder(w).Encode(resp)
}

// cleanAzureError extracts a user-friendly message from
// verbose Azure CLI error output. It looks for the
// "Message:" line and strips the repeated region list.
func cleanAzureError(raw string) string {
	// Look for the Message: line in the Azure error.
	if idx := strings.Index(
		raw, "\nMessage: ",
	); idx >= 0 {
		msg := raw[idx+len("\nMessage: "):]
		// Trim at the next newline.
		if nl := strings.Index(msg, "\n"); nl >= 0 {
			msg = msg[:nl]
		}
		return strings.TrimSpace(msg)
	}

	// Look for ERROR: prefix from az CLI.
	if idx := strings.Index(
		raw, "ERROR: ",
	); idx >= 0 {
		msg := raw[idx+len("ERROR: "):]
		// Take only the first line.
		if nl := strings.Index(msg, "\n"); nl >= 0 {
			msg = msg[:nl]
		}
		return strings.TrimSpace(msg)
	}

	return raw
}
