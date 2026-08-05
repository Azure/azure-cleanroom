package dashboard

import (
	"context"
	"fmt"
	"html/template"
	"net"
	"net/http"
	"time"

	log "github.com/sirupsen/logrus"
	"k8s.io/client-go/dynamic"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/rest"
)

// Server is the dashboard HTTP server.
type Server struct {
	config    *rest.Config
	dynClient dynamic.Interface
	clientset *kubernetes.Clientset
	hub       *Hub
	templates *template.Template
	namespace string
}

// NewServer creates a new dashboard server.
func NewServer(
	config *rest.Config,
	namespace string,
) (*Server, error) {
	dynClient, err := dynamic.NewForConfig(config)
	if err != nil {
		return nil, fmt.Errorf(
			"creating dynamic client: %w", err,
		)
	}

	clientset, err := kubernetes.NewForConfig(config)
	if err != nil {
		return nil, fmt.Errorf(
			"creating clientset: %w", err,
		)
	}

	tmpl, err := template.New("").Funcs(
		templateFuncs(),
	).ParseFS(templateFS, "templates/*.html")
	if err != nil {
		return nil, fmt.Errorf(
			"parsing templates: %w", err,
		)
	}

	return &Server{
		config:    config,
		dynClient: dynClient,
		clientset: clientset,
		hub:       NewHub(),
		templates: tmpl,
		namespace: namespace,
	}, nil
}

// Run starts the HTTP server and the K8s watcher.
func (s *Server) Run(
	ctx context.Context, port int,
) (int, error) {
	// Find a free port if needed.
	localPort := port
	for i := 0; i < 10; i++ {
		ln, err := net.Listen(
			"tcp",
			fmt.Sprintf(":%d", localPort),
		)
		if err == nil {
			ln.Close()
			break
		}
		localPort++
	}

	mux := http.NewServeMux()
	mux.Handle(
		"GET /static/",
		http.FileServerFS(staticFS),
	)
	mux.HandleFunc("GET /", s.handleIndex)
	mux.HandleFunc(
		"GET /api/topology/{name}",
		s.handleTopology,
	)
	mux.HandleFunc(
		"GET /api/events/{name}",
		s.handleEvents,
	)
	mux.HandleFunc(
		"GET /api/conditions/{name}",
		s.handleConditions,
	)
	mux.HandleFunc(
		"GET /api/stream",
		s.handleSSE,
	)
	mux.HandleFunc(
		"POST /api/reconcile/{name}",
		s.handleReconcile,
	)
	mux.HandleFunc(
		"DELETE /api/environment/{name}",
		s.handleDelete,
	)
	mux.HandleFunc(
		"POST /api/environment",
		s.handleCreate,
	)
	mux.HandleFunc(
		"GET /api/azure-context",
		s.handleAzureContext,
	)

	srv := &http.Server{
		Addr:              fmt.Sprintf(":%d", localPort),
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
	}

	// Start watchers.
	go s.watchResources(ctx)
	go s.hub.Run(ctx)

	go func() {
		<-ctx.Done()
		shutdownCtx, cancel := context.WithTimeout(
			context.Background(), 5*time.Second,
		)
		defer cancel()
		if err := srv.Shutdown(shutdownCtx); err != nil {
			log.WithError(err).Warn(
				"HTTP server shutdown error",
			)
		}
	}()

	go func() {
		log.WithField("port", localPort).Info(
			"Dashboard server started",
		)
		if err := srv.ListenAndServe(); err != nil &&
			err != http.ErrServerClosed {
			log.WithError(err).Error(
				"HTTP server error",
			)
		}
	}()

	return localPort, nil
}

func templateFuncs() template.FuncMap {
	return template.FuncMap{
		"statusIcon": func(status string) string {
			switch status {
			case "True":
				return "✓"
			case "False":
				return "✗"
			default:
				return "○"
			}
		},
		"statusClass": func(status, reason string) string {
			switch {
			case status == "True":
				return "status-ready"
			case reason == "Failed":
				return "status-failed"
			default:
				return "status-pending"
			}
		},
		"phaseClass": func(phase string) string {
			switch phase {
			case "Ready", "Open", "Active", "Running":
				return "phase-ready"
			case "Failed":
				return "phase-failed"
			case "Deleting":
				return "phase-deleting"
			default:
				return "phase-pending"
			}
		},
		"kindIcon": func(kind string) string {
			switch kind {
			case "Environment":
				return "🏠"
			case "CcfMember":
				return "👤"
			case "CcfNetwork":
				return "🔗"
			case "GovernanceService":
				return "⚖️"
			case "GovernanceContract":
				return "📜"
			case "WorkloadGovernance":
				return "🛡️"
			case "Cluster":
				return "☸"
			case "ModelRegistration":
				return "🤖"
			case "ModelDeployment":
				return "📦"
			default:
				return "📄"
			}
		},
		"specField": func(
			raw map[string]interface{},
			field string,
		) string {
			spec, ok :=
				raw["spec"].(map[string]interface{})
			if !ok {
				return ""
			}
			val, _ := spec[field].(string)
			return val
		},
		"timeAgo": func(t time.Time) string {
			if t.IsZero() {
				return ""
			}
			d := time.Since(t)
			switch {
			case d < time.Minute:
				return fmt.Sprintf("%ds ago", int(d.Seconds()))
			case d < time.Hour:
				return fmt.Sprintf("%dm ago", int(d.Minutes()))
			case d < 24*time.Hour:
				return fmt.Sprintf(
					"%dh%dm ago",
					int(d.Hours()),
					int(d.Minutes())%60,
				)
			default:
				return fmt.Sprintf(
					"%dd ago", int(d.Hours()/24),
				)
			}
		},
	}
}
