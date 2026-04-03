package admission

import (
	"log"
	"os"
	"strings"
)

// ChainController chains multiple admission controllers together
type ChainController struct {
	controllers []Controller
	logger      *log.Logger
}

// NewChainController creates a new chain controller
func NewChainController(controllers ...Controller) *ChainController {
	return &ChainController{
		controllers: controllers,
		logger:      log.New(os.Stdout, "[admission-chain] ", log.LstdFlags|log.Lmicroseconds),
	}
}

// Name returns the name of the controller
func (c *ChainController) Name() string {
	names := make([]string, len(c.controllers))
	for i, ctrl := range c.controllers {
		names[i] = ctrl.Name()
	}
	return "chain[" + strings.Join(names, ",") + "]"
}

// Admit evaluates all controllers in the chain
// All controllers must allow for the pod to be admitted
func (c *ChainController) Admit(req *Request) *Decision {
	var warnings []string
	var mutatedPod map[string]interface{}

	for _, ctrl := range c.controllers {
		c.logger.Printf("Evaluating controller: %s", ctrl.Name())

		// If a previous controller mutated the pod, use the mutated version
		evalReq := req
		if mutatedPod != nil {
			evalReq = &Request{
				Namespace: req.Namespace,
				Name:      req.Name,
				Pod:       mutatedPod,
				RawPod:    req.RawPod, // Keep original raw for reference
			}
		}

		decision := ctrl.Admit(evalReq)

		// Collect warnings
		warnings = append(warnings, decision.Warnings...)

		// If denied, return immediately
		if !decision.Allowed {
			c.logger.Printf("Controller %s denied: %s", ctrl.Name(), decision.Reason)
			decision.Warnings = warnings
			return decision
		}

		// Track mutations
		if decision.MutatedPod != nil {
			mutatedPod = decision.MutatedPod
		}

		c.logger.Printf("Controller %s allowed: %s", ctrl.Name(), decision.Reason)
	}

	return &Decision{
		Allowed:    true,
		Reason:     "all controllers allowed",
		MutatedPod: mutatedPod,
		Warnings:   warnings,
	}
}

// AddController adds a controller to the chain
func (c *ChainController) AddController(ctrl Controller) {
	c.controllers = append(c.controllers, ctrl)
}
