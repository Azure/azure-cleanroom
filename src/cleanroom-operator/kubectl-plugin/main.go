package main

import (
	"os"

	"github.com/Azure/azure-cleanroom/cleanroom-operator/kubectl-plugin/app"
)

func main() {
	if err := app.NewRootCmd().Execute(); err != nil {
		os.Exit(1)
	}
}
