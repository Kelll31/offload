# Rust rules

## Style
- Use rustfmt and clippy for formatting and linting
- Use snake_case for functions, variables and modules, CamelCase for types

## Errors
- Use Result<T, E> and the ? operator for error propagation
- Use thiserror for library errors, anyhow for application entry points
- Avoid unwrap() and expect() in library code outside of tests

## Ownership
- Prefer borrowing (&) over cloning to minimize allocations
- Take &str and &[T] in function parameters for flexibility
- Prefer iterators over index-based loops for cleaner iteration

## Concurrency
- Ensure types implement Send/Sync for thread safety
- Use tokio for async I/O and never block the async runtime
- Prefer channels or Arc<Mutex<_>> sparingly for shared state

## Tests
- Place unit tests in a #[cfg(test)] mod within the source file
- Place integration tests in the tests/ directory at project root

## Avoid
- Avoid unsafe blocks unless accompanied by a SAFETY comment
- Avoid needless lifetimes; prefer elided lifetimes where possible
