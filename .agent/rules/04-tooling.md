---
trigger: always_on
description: Tooling and MCP Usage
---

# Tooling and MCP Usage

## MCP Servers
*   **Usage**: Actively utilize available MCP servers to extend capabilities.
*   **Perplexity-Ask**:
    *   **Purpose**: Use for real-time web search and answering complex technical questions that require up-to-date information or external documentation not present in the codebase.
    *   **Key Tool**: `ask_perplexity_ask` - Engages in a conversation using the Sonar API for detailed technical queries.
*   **Code-Index-MCP**:
    *   **Purpose**: Use for deep codebase exploration, symbol extraction, and advanced code search. This is superior to standard grep for understanding code structure.
    *   **Key Tools**:
        *   `search_code_advanced`: Perform paginated, regex-enabled code searches across the project.
        *   `get_symbol_body`: Retrieve the full source code of specific functions or classes without loading entire files.
        *   `get_file_summary`: Get a high-level overview of a file including imports, definitions, and complexity.
        *   `find_files`: Quickly locate files matching specific patterns using the in-memory index.
    *   **Management**: Use `build_deep_index` or `refresh_index` when significant changes are made to ensure search results are accurate.

## Repomix Runner
*   **Purpose**: Use Repomix for packing the codebase and gathering context for large-scale analysis or refactoring.
*   **Usage**: Run Repomix via `run_command` (bash) when you need to understand the entire project structure or a specific module in depth.
*   **Reference**: Follow prompt examples from `https://repomix.com/guide/prompt-examples`.

## Git
*   **Commits**: Write clear, descriptive commit messages.
*   **Branching**: Use feature branches for new development.
*   **Merging**: Prefer rebase over merge for cleaner history.