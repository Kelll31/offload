# Shell (PowerShell / bash) rules

## PowerShell
- Use Verb-Noun names with approved verbs (e.g., Get-Process)
- Set $ErrorActionPreference = 'Stop' to fail fast on errors
- Use param() blocks with types and validation attributes
- Quote paths with spaces and check $LASTEXITCODE after native commands

## Bash
- Start scripts with a proper shebang (e.g., #!/bin/bash)
- Use set -euo pipefail for strict error handling
- Quote all variable expansions to prevent word splitting
- Use [[ ]] for tests, trap for cleanup, and mktemp for temp files

## Safety
- Never run destructive commands without a dry-run or confirm switch
- Validate all inputs before processing
- Never eval user input to prevent injection
- Avoid storing secrets in command lines or environment variables

## Avoid
- Parsing ls output for file names
- Using aliases in scripts for consistency
- Invoke-Expression (iex) for dynamic execution
- Relying on the current directory for paths
