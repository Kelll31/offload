# C++ (modern) rules

## Style
- Use const correctness and prefer const references for non-mutating parameters
- Use std::string_view or std::span for non-owning read-only parameters
- Prefer auto where the type is obvious; use enum class for enumerations

## Memory
- Use RAII everywhere; prefer std::unique_ptr by default, std::shared_ptr only for shared ownership
- Avoid raw new/delete; use std::make_unique and std::make_shared
- Prefer stack allocation for small, short-lived objects

## Errors
- Use exceptions or std::expected consistently; prefer noexcept where no throw is possible
- Never throw from destructors; use std::terminate or log silently
- Avoid raw pointers for ownership; use optional for nullable values

## Concurrency
- Use std::jthread or std::mutex with std::lock_guard/std::scoped_lock
- Ensure no data races; use std::atomic only when performance demands it
- Prefer higher-level concurrency primitives over raw threads

## Tests
- Use GoogleTest or Catch2; place tests near source or in a tests/ directory
- Write one behavior per test case for clear failure reporting
- Use mock objects for dependencies to isolate unit tests

## Avoid
- Avoid C-style casts; use static_cast, dynamic_cast, or reinterpret_cast
- Avoid macros for constants; use constexpr or enum class
- Avoid using namespace std in headers to prevent name collisions
- Avoid manual memory management; let RAII handle lifetimes
