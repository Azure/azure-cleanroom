// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Package runner provides a thin subprocess wrapper with logging.
package runner

import (
	"fmt"
	"os/exec"
	"strings"

	log "github.com/sirupsen/logrus"
)

// CommandError is returned when a subprocess exits with a non-zero code.
type CommandError struct {
	Args       []string
	ExitCode   int
	Output     string
}

func (e *CommandError) Error() string {
	return fmt.Sprintf(
		"command %v failed with exit code %d:\n%s",
		e.Args, e.ExitCode, e.Output,
	)
}

// RunCmd executes a command, logs its output, and returns an error on
// non-zero exit when check is true.
func RunCmd(args []string, check bool, env []string) error {
	log.Infof("Running: %s", strings.Join(args, " "))

	cmd := exec.Command(args[0], args[1:]...)
	if len(env) > 0 {
		cmd.Env = env
	}

	output, err := cmd.CombinedOutput()
	outStr := string(output)

	for _, line := range strings.Split(strings.TrimSpace(outStr), "\n") {
		if line != "" {
			log.Infof("  %s", line)
		}
	}

	if err != nil {
		exitCode := -1
		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode = exitErr.ExitCode()
		}
		if check {
			return &CommandError{
				Args:     args,
				ExitCode: exitCode,
				Output:   strings.TrimSpace(outStr),
			}
		}
	}
	return nil
}

// RunCmdOutput executes a command and returns its stdout as a string.
func RunCmdOutput(args []string) (string, error) {
	log.Infof("Running: %s", strings.Join(args, " "))

	cmd := exec.Command(args[0], args[1:]...)
	output, err := cmd.CombinedOutput()
	outStr := strings.TrimSpace(string(output))

	if err != nil {
		exitCode := -1
		if exitErr, ok := err.(*exec.ExitError); ok {
			exitCode = exitErr.ExitCode()
		}
		return outStr, &CommandError{
			Args:     args,
			ExitCode: exitCode,
			Output:   outStr,
		}
	}
	return outStr, nil
}
