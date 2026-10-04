# React / frontend rules

## Components
- Use function components and hooks exclusively; avoid class components.
- Keep components small and single-purpose to enhance reusability.
- Define strict TypeScript interfaces for all props; avoid any types.

## State
- Lift state minimally to the lowest common ancestor sharing the state.
- Derive values from existing state rather than duplicating them.
- Manage server state via a dedicated query library (e.g., React Query).

## Effects
- Reserve effects only for synchronization with external systems.
- Include complete dependency arrays; lint errors are forbidden.
- Return cleanup functions to unsubscribe from external resources.

## Accessibility
- Use semantic HTML elements to convey document structure.
- Attach labels to all inputs; associate them explicitly.
- Ensure full keyboard navigation and focus management support.

## Tests
- Use React Testing Library queries by role, text, or label.
- Test user behavior and output, not internal component implementation.
- Mock external dependencies to isolate component logic.

## Avoid
- Never use array index as key for dynamic or sortable lists.
- Avoid prop drilling through many levels; use Context or composition.
- Memoize heavy computations to prevent unnecessary re-renders.
