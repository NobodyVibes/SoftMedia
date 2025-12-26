---
trigger: always_on
description: Technology Stack and Best Practices
---

# Technology Stack and Best Practices

## Backend: C# / .NET 8
*   **Framework**: Use ASP.NET Core Web API.
*   **Code Style**: Follow standard C# coding conventions (PascalCase for public members, camelCase for private fields, etc.).
*   **Async/Await**: Use asynchronous programming (`async`/`await`) for all I/O operations (database, file system, network).
*   **LINQ**: Use LINQ for data manipulation where appropriate for readability.
*   **Entity Framework Core**: Use EF Core for data access. Use Migrations for schema changes.

## Frontend: React + Vite + Tailwind
*   **Language**: TypeScript is mandatory. Strict mode enabled.
*   **Components**: Use Functional Components with Hooks. Avoid Class Components.
*   **Styling**:
    *   **Tailwind CSS**: Use for all styling.
    *   **Theme**: Adhere to the project's color palette (Blue-Violet gradient).
    *   **Responsiveness**: Ensure designs work on Mobile and Desktop.
    *   **Focus States (TV-Ready)**: Always pair `hover:` with `focus-visible:` states for keyboard/remote navigation.
*   **State Management**:
    *   `TanStack Query` for server state (API data).
    *   `Zustand` for global UI state.
    *   `React.useState` for local component state.
*   **Build Tool**: Use Vite. Do not use Create React App.

## Multi-Platform Targets
*   **Desktop + WebOS (Simultaneous)**:
    *   Shared layout: `MainLayout.tsx` with Sidebar navigation.
    *   WebOS requires spatial navigation (Arrow keys, not Tab).
    *   Use `<button>` elements with `tabIndex` for all interactables.
*   **Mobile (Deferred)**:
    *   Separate layout: `MobileLayout.tsx` with Bottom Tab Bar.
    *   Build after Desktop UI is stable.
    *   Reuse shared components (`MediaCard`, `VideoPlayer`).

## General
*   **No Anti-Patterns**: Avoid "God Classes", magic numbers, and hardcoded strings.
*   **Error Handling**: Implement global error handling (middleware in .NET, Error Boundaries in React).
