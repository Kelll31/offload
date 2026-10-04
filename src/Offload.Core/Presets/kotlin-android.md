# Kotlin / Android rules

## Style
- Prefer val over var; use data classes for DTOs and sealed interfaces for state machines
- Enforce null-safety with ? and Elvis operator; avoid !! operator

## Android
- Use ViewModel with StateFlow for unidirectional data flow; hoist state in Jetpack Compose
- Avoid context leaks by using application context; store strings in strings.xml

## Coroutines
- Use structured concurrency via viewModelScope or lifecycleScope; inject Dispatchers
- Use withContext(Dispatchers.IO) for blocking work; never use GlobalScope; collect flows lifecycle-aware

## Tests
- Use JUnit 5 with kotlinx-coroutines-test for coroutine logic; use MockK for mocking
- Write Compose UI tests using createComposeRule for UI component verification

## Avoid
- Avoid !! operator to prevent unexpected NullPointerExceptions
- Avoid blocking the main thread; offload heavy work to background threads
- Avoid business logic inside composables; keep composables declarative
