# C (embedded) rules

## Style
- Use fixed-width types from stdint.h (e.g., uint32_t, int8_t) for all integer variables
- Use const and static for internal linkage to enforce encapsulation and immutability
- Use include guards (#ifndef HEADER_H) in headers; one module = one .c + one .h pair

## Memory
- Avoid dynamic allocation after initialization; prefer static or stack allocation
- Check every malloc/calloc result for NULL before use
- Use bounded buffers with explicit sizes; avoid Variable Length Arrays (VLAs)

## Errors
- Return error codes (int/enums) and check them at every call site
- Use a single cleanup path via goto for resource deallocation in complex functions

## Concurrency
- Use volatile for hardware registers to prevent compiler optimization
- Keep ISRs short; protect shared data with critical sections (disable interrupts)
- Avoid blocking calls in ISRs

## Tests
- Use Unity or CMocka for unit tests
- Abstract hardware behind an interface layer so logic is testable on host

## Avoid
- Avoid gets, strcpy, and sprintf; use snprintf and strncpy instead
- Avoid magic numbers; use named constants or enums
- Avoid implicit conversions and recursion
