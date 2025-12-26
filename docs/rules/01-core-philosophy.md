---
trigger: always_on
description: Core Philosophy and Development Workflow
---

# Core Philosophy and Workflow

## Back-to-Front Development
*   **Principle**: Always implement backend logic before frontend implementation.
*   **Requirement**: Ensure API endpoints, database schemas, and business logic are fully functional and tested before building the UI components that consume them.
*   **Verification**: Verify backend functionality with `curl` or unit tests before writing React code.

## Spaghetti Code Prevention
*   **Modularity**: Enforce strict separation of concerns.
    *   **Backend**: Controllers -> Services -> Repositories -> Database.
    *   **Frontend**: Page Components -> Feature Components -> UI Components (dumb) -> Hooks/Utils.
*   **Dependency Injection**: Use .NET Core's built-in DI container. Avoid static global state.
*   **Single Responsibility**: Each class or component should have one clear purpose.

## Global Checklist
*   **Requirement**: Maintain a `.docs/project_checklist.md` file.
*   **Usage**:
    *   Track high-level project progress.
    *   Update context across sessions.
    *   Mark completed features and note pending tasks.
*   **Format**: Markdown checklist with clear status indicators.

## Project Philosophy
*   **Local-First**: Prioritize local storage and processing. Avoid cloud dependencies.
*   **Privacy**: Do not implement tracking or analytics.
*   **Aesthetics**: Adhere to the "Dark Mode" UI with Blue-Violet gradient as defined in the SDD.

## Universal Client Philosophy
*   **Single Codebase**: Build one React application that adapts to Desktop, WebOS/TV, and Mobile.
*   **Desktop + TV First**: Desktop and WebOS share the same layout paradigm (Sidebar). Build them simultaneously.
*   **Mobile Later**: Mobile requires a distinct layout (Bottom Tab Bar). Defer until Desktop is stable.
*   **TV-Readiness Rules** (Apply to all new components):
    *   Use `<button>` for clickables. Avoid `<div onClick>` without `role="button"` and `tabIndex`.
    *   Always pair hover with focus: `hover:bg-white/10 focus-visible:bg-white/10 focus-visible:ring-2`.
    *   Ensure all interactive elements are Tab-reachable.

