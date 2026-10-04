# PHP / Laravel rules

## Style
- Always declare `strict_types=1` at the top of files
- Follow PSR-12 coding standards; use typed properties and return types
- Use PHP 8.2 features: enums, readonly classes, constructor promotion

## Laravel
- Use Form Requests for validation; Eloquent with `with()` for eager loading
- Use Policies for authorization; Queues for slow background work
- Use `config()` helper, never `env()` outside config files
- Use migrations for every schema change

## Errors
- Throw specific, meaningful exceptions (e.g., `ModelNotFoundException`)
- Report errors via the exception handler's `render()` method

## Tests
- Use Pest or PHPUnit; prefer Pest for concise syntax
- Write feature tests using `RefreshDatabase` trait and factories

## Avoid
- Avoid fat controllers; extract logic to services or actions
- Avoid raw SQL with interpolated input strings
- Avoid mass assignment without defining `$fillable` properties
- Avoid business logic in Blade templates
