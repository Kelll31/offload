# Swift / iOS rules

## Style
- Follow Swift API Design Guidelines; use camelCase for methods/vars, PascalCase for types
- Prefer let over var; prefer structs over classes unless identity or inheritance is needed
- Use guard for early exit; unwrap optionals safely with if let or ??, never force unwrap

## SwiftUI
- Keep views small and composable; extract complex logic into separate view structs
- Use @State for local view state, @Binding for shared mutable state, @Observable for model
- State is the single source of truth; derive UI from state, never mutate state directly in UI

## Concurrency
- Use async/await for asynchronous operations; prefer structured concurrency (TaskGroup)
- Use actors for shared mutable state; annotate UI closures with @MainActor
- Respect cancellation by checking Task.isCancelled and using withTaskCancellationHandler

## Errors
- Use throws with typed error enums conforming to Error; define specific error cases
- Use do/catch blocks to handle errors specifically; avoid generic catch blocks where possible
- Propagate errors up using the ? operator in async contexts; handle at boundaries

## Tests
- Use Swift Testing or XCTest; name tests by behavior (e.g., testViewModelUpdatesUI)
- Test view logic in isolation; mock dependencies for network and storage calls
- Keep tests fast and deterministic; avoid flaky timing-dependent assertions

## Avoid
- Avoid force unwrap (!) and try!; handle optionals and errors explicitly
- Avoid retain cycles by using [weak self] in closures and delegates
- Avoid massive view bodies; decompose complex views into smaller, reusable components
