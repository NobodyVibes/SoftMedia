---
trigger: always_on
description: Tooling and MCP Usage
---

# Tooling and MCP Usage

## MCP Servers
*   **Usage**: Actively utilize available MCP servers to extend capabilities.
*   **Discovery**: Use `list_resources` to find available tools and data.
*   **Context**: Use MCP tools to fetch external documentation or context when needed.

## Repomix Runner
*   **Purpose**: Use Repomix for packing the codebase and gathering context for large-scale analysis or refactoring.
*   **Usage**: Run Repomix via `run_command` (bash) when you need to understand the entire project structure or a specific module in depth.
*   **Reference**: Follow prompt examples from `https://repomix.com/guide/prompt-examples`.

## Git
*   **Commits**: Write clear, descriptive commit messages.
*   **Branching**: Use feature branches for new development.
