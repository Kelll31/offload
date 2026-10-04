# Ruby / Rails rules

## Style
- Configure RuboCop for formatting and linting
- Use snake_case for methods and variables, CamelCase for classes and modules
- Enable frozen_string_literal: true in all files
- Keep methods small and use guard clauses for early returns
- Prefer Enumerable methods over manual loops for iteration

## Rails
- Keep controllers skinny; move logic to service objects
- Use strong parameters for mass assignment protection
- Define scopes in models for reusable query logic
- Use includes or joins to avoid N+1 queries
- Offload slow work to background jobs

## Errors
- Rescue specific exceptions, never bare rescue
- Raise custom error classes for domain-specific failures

## Tests
- Use RSpec or Minitest with FactoryBot for fixtures
- Prefer request specs over controller specs
- Focus each example on one expectation

## Avoid
- Avoid callbacks with side effects
- Avoid default_scope and monkey patching core classes
- Avoid raw SQL with interpolated input
