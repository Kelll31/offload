# C# / .NET rules

## Style
- Use file-scoped namespaces and enable nullable reference types.
- PascalCase for public members; _camelCase for private fields.
- Use records for DTOs; prefer sealed classes by default.
- Primary constructors are acceptable for concise types.

## Errors
- Throw exceptions only for exceptional cases, not control flow.
- Validate arguments using ArgumentNullException.ThrowIfNull.
- Never swallow exceptions; always log or rethrow appropriately.

## Async
- Use async all the way; avoid mixing synchronous and asynchronous code.
- Prefer Task over async void; pass CancellationToken where applicable.
- Use ConfigureAwait(false) in library code to avoid deadlocks.
- Avoid .Result or .Wait() to prevent blocking threads unnecessarily.

## Tests
- Use xUnit for testing framework.
- Name tests Method_Condition_Expected for clarity.
- Structure test methods using Arrange-Act-Assert pattern.

## Avoid
- Avoid static mutable state to ensure thread safety and testability.
- Prefer generics over reflection when type is known at compile time.
- Do not add new dependencies unless strictly necessary for functionality.
