# Java / Spring rules

## Style
- Use records for immutable data transfer objects
- Use Optional only as return type, not for fields
- Prefer streams for transformations, plain loops when clearer
- Declare variables final where possible

## Spring
- Use constructor injection exclusively
- Keep controllers thin; delegate logic to services
- Annotate service methods with @Transactional
- Use DTOs at API boundaries, entities internally
- Validate input with jakarta.validation annotations

## Errors
- Throw specific, domain-relevant exceptions
- Use @ControllerAdvice for global HTTP error mapping
- Never swallow exceptions; always log or rethrow
- Use SLF4J placeholders for logging, not string concatenation

## Tests
- Use JUnit 5 and Mockito for unit tests
- Use @SpringBootTest sparingly for integration
- Prefer slice tests like @WebMvcTest and @DataJpaTest
- Mock external dependencies; test service logic directly

## Avoid
- Avoid field injection (@Autowired on fields)
- Avoid returning JPA entities directly from controllers
- Avoid System.out.println; use SLF4J Logger instead
- Avoid mixing persistence logic in controller layers
