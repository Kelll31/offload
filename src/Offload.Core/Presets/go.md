# Go rules

## Style
- Use gofmt or goimports; do not manually format.
- Use short lowercase package names; import paths must match directory names.
- Use MixedCaps for exported names; unexported names must be lowercase.
- Accept interfaces, return structs; keep interfaces small and explicit.

## Errors
- Return errors explicitly; never panic for normal control flow.
- Wrap errors with fmt.Errorf("...: %w", err) to preserve context.
- Check wrapped errors using errors.Is and errors.As.
- Do not ignore errors; handle or propagate them immediately.

## Concurrency
- Pass context.Context as the first parameter to all blocking functions.
- Goroutines must have a clear owner; ensure they exit when done.
- Close channels only from the sender; do not close from receivers.
- Use sync.WaitGroup or errgroup for coordinating goroutine lifecycles.

## Tests
- Write tests in *_test.go files using the standard testing package.
- Use table-driven tests with t.Run for each test case scenario.
- Run go vet and go test -race to catch issues early.
- Keep tests deterministic; avoid external dependencies where possible.

## Avoid
- Avoid global mutable state; prefer dependency injection or packages.
- Avoid ignoring errors with blank identifier (_); handle or return them.
- Avoid side effects in init(); keep initialization logic explicit and small.
- Avoid deep nesting; use early returns to reduce indentation levels.
