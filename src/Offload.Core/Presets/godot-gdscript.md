# Godot 4 (GDScript) rules

## Style
- Use static typing: var x: int, func f() -> void
- Use snake_case for functions and variables, PascalCase for class_name
- Use UPPER_CASE for constants

## Scenes
- Keep scenes small and reusable; prefer composition over inheritance
- Use call down and signal up for communication
- Access unique-named nodes via %Name instead of long node paths

## Signals
- Name signals in past tense (e.g., player_died)
- Connect in code using signal.connect(handler)
- Disconnect signals in _exit_tree to prevent leaks

## Performance
- Use _physics_process with delta for physics, _process for visuals
- Avoid per-frame get_node; cache references with @onready
- Use groups for bulk queries and object pools for frequent instantiation

## Tests
- Use GUT or gdUnit4 for unit and integration tests
- Test node logic independently of rendering

## Avoid
- Avoid Godot 3 syntax (yield, onready var, connect with strings)
- Avoid using autoload for everything; pass dependencies instead
- Avoid hard node paths across scenes
