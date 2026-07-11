---
name: "React Vite"
description: "Technical guidance for React, Vite, TypeScript, components, API access, styling, and frontend tests."
---

# React Vite Skill

Use this skill when working with React, Vite, TypeScript, frontend components, routing, API clients, or frontend tests.

Follow the existing project conventions first, then common React/Vite conventions.

## Project checks

- Check `package.json`, Vite config, TypeScript config, ESLint config, and existing test setup before changing patterns.
- Do not replace the router, state library, styling approach, build system, or test framework unless explicitly requested.
- Do not introduce new packages unless they solve a clear problem.

## React and TypeScript

- Prefer function components.
- Use explicit types for public component props.
- Keep components small and focused.
- Prefer composition over large configurable components.
- Avoid unnecessary global state.
- Prefer derived state over duplicated state.
- Do not suppress TypeScript errors without a clear reason.

## API and state

- Keep API calls in dedicated client/service modules.
- Handle loading, empty, error, and success states explicitly.
- Do not silently ignore failed requests.
- Keep DTOs and frontend models clear.
- Avoid calling backend endpoints directly from many unrelated components.

## Vite

- Use Vite-native environment variables.
- Only expose frontend variables with the configured public prefix, usually `VITE_`.
- Do not put secrets in frontend environment variables.
- Keep Vite configuration minimal.

## Styling and accessibility

- Follow the existing styling approach.
- Use semantic HTML where possible.
- Use buttons for actions and links for navigation.
- Ensure interactive elements are keyboard accessible.
- Provide labels for inputs.

## Tests

- Use the existing test framework.
- If no framework exists, prefer Vitest.
- Prefer React Testing Library for component behavior.
- Test behavior, not implementation details.
- Mock network boundaries, not the component under test.
