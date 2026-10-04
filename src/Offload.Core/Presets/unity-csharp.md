# Unity (C#) rules

## Style
- Use [SerializeField] private fields instead of public fields; PascalCase methods; proper namespaces
- Follow C# naming conventions; use readonly for immutable data

## Lifecycle
- Use Awake for self-initialization; Start for cross-references between components
- Use OnEnable/OnDisable for subscribing/unsubscribing to events; never rely on script execution order

## Performance
- Avoid allocations in Update: cache components, reuse lists, avoid LINQ and string concatenation per frame
- Use object pooling for frequent instantiation/destruction; use Time.deltaTime for movement; FixedUpdate for physics

## Architecture
- Use ScriptableObjects for data and events; keep MonoBehaviours small and focused
- Prefer interfaces over GameObject.Find for decoupling

## Tests
- Use Unity Test Framework EditMode tests for logic without scene dependencies
- Use PlayMode tests for runtime behavior and component interactions

## Avoid
- Avoid GameObject.Find and SendMessage in runtime code for performance and type safety
- Avoid GetComponent in Update; cache references instead
- Avoid coroutines that never stop or lack cancellation
