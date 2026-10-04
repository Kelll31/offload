# TypeScript / Node rules

## Style
- Use strict mode in tsconfig.json
- Prefer const/let over var; never use var
- Use camelCase for variables/functions; PascalCase for types/classes
- Use ES modules (import/export); avoid CommonJS

## Types
- Avoid 'any'; use 'unknown' with type narrowing
- Prefer interfaces for object shapes; types for unions
- Use discriminated unions for state machines
- Mark properties readonly where possible

## Errors
- Throw Error subclasses (e.g., CustomError)
- Never throw strings or non-Error objects
- Handle promise rejections via catch or try/catch

## Async
- Use async/await; avoid raw .then() chains
- Use Promise.all for independent parallel work
- Never leave floating promises; await all
- Use AbortController for request cancellation

## Tests
- Use vitest or jest for unit testing
- Write describe/it blocks with behaviour names
- Mock external dependencies explicitly
- Test edge cases and error paths

## Avoid
- Avoid non-null assertions (!) where possible
- Avoid enums; use const objects or unions
- Avoid sync fs calls (readFileSync) in servers
- Avoid global state; use dependency injection
