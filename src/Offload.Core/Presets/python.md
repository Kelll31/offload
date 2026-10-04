# Python rules

## Style
- Follow PEP 8 strictly; use snake_case for variables and functions
- Use type hints on all public function signatures
- Prefer dataclasses or Pydantic models for structured data
- Use pathlib.Path instead of os.path for file operations
- Use f-strings for string formatting and context managers for resources

## Errors
- Catch specific exceptions (e.g., ValueError), never use bare except
- Raise exceptions with context using 'raise ... from e' syntax
- Use logging instead of print statements for debugging output

## Concurrency
- Use asyncio for IO-bound operations; never block the event loop
- Avoid blocking calls in async code; use await for all IO
- Use multiprocessing or threading for CPU-bound parallel work

## Tests
- Use pytest; name test files and functions with 'test_' prefix
- Use fixtures for setup/teardown and parametrize for data-driven tests
- Keep tests isolated and idempotent; mock external dependencies

## Avoid
- Avoid mutable default arguments (e.g., use None instead of [])
- Avoid global state; prefer dependency injection or class attributes
- Avoid wildcard imports (from module import *); import explicitly
