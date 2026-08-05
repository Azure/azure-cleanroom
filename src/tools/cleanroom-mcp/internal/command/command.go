// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package command

import (
	"bytes"
	"context"
	"fmt"
	"os/exec"
	"strings"
	"time"

	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/logger"
)

// Result holds the output of a command execution.
type Result struct {
	Output string
	Error  string
}

// dangerousPatterns are shell meta-characters that could be used for command
// injection. Commands containing any of these are rejected.
var dangerousPatterns = []string{
	";", "|", "&", "`", "&&", "||", ">>", ">", "<", "$(", "${",
}

// Execute runs a CLI command with the given arguments and timeout.
// The command string is split safely (no shell invocation) and both stdout
// and stderr are captured.
func Execute(ctx context.Context, command string, timeout int) (*Result, error) {
	if err := validateCommand(command); err != nil {
		return nil, err
	}

	timeoutDuration := time.Duration(timeout) * time.Second
	cmdCtx, cancel := context.WithTimeout(ctx, timeoutDuration)
	defer cancel()

	args := strings.Fields(command)
	if len(args) == 0 {
		return nil, fmt.Errorf("empty command")
	}

	logger.Debugf("Executing command: %s", command)

	cmd := exec.CommandContext(cmdCtx, args[0], args[1:]...)

	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	err := cmd.Run()

	if cmdCtx.Err() == context.DeadlineExceeded {
		return nil, fmt.Errorf("command timed out after %d seconds", timeout)
	}

	if err != nil {
		errMsg := stderr.String()
		if errMsg == "" {
			errMsg = err.Error()
		}
		return &Result{
			Output: stdout.String(),
			Error:  strings.TrimSpace(errMsg),
		}, fmt.Errorf("command failed: %s", strings.TrimSpace(errMsg))
	}

	return &Result{
		Output: stdout.String(),
	}, nil
}

// validateCommand checks for shell injection patterns in the command string.
func validateCommand(command string) error {
	for _, pattern := range dangerousPatterns {
		if strings.Contains(command, pattern) {
			return fmt.Errorf(
				"command contains dangerous pattern '%s'; "+
					"shell features like pipes, redirects, and command "+
					"substitution are not allowed", pattern)
		}
	}
	return nil
}
