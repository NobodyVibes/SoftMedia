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
*   **State Management**:
    *   `TanStack Query` for server state (API data).
    *   `Zustand` for global UI state.
    *   `React.useState` for local component state.
*   **Build Tool**: Use Vite. Do not use Create React App.

## General
*   **No Anti-Patterns**: Avoid "God Classes", magic numbers, and hardcoded strings.
*   **Error Handling**: Implement global error handling (middleware in .NET, Error Boundaries in React).
