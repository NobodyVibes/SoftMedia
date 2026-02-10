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
    *   **Core Search Tools**:
        *   `search_code_advanced`: **(Primary Search Tool)** Perform paginated, regex-enabled, code searches across the project. Auto-selects the best search tool (ugrep/ripgrep). Use this for finding usage patterns, definitions, or specific strings.
        *   `find_files`: Quickly locate files matching specific patterns (keywords or globs) using the in-memory index. Faster than listing directories for known patterns.
    *   **Context & Understanding**:
        *   `get_file_summary`: Get a high-level overview of a file including line count, definitions (classes/functions), imports, and complexity metrics. Use this before reading a large file to get the "lay of the land".
        *   `get_symbol_body`: Retrieve the *exact* source code of a specific function, class, or method without loading the entire file. Essential for token efficiency when you only need to see how a specific method is implemented.
    *   **Indexing & Maintenance**:
        *   `build_deep_index`: **(Critical)** Performs a complete re-index of the project. Run this if search results seem stale or after massive code changes.
        *   `refresh_index`: lighter-weight index refresh. Use after git operations or medium-sized changes.
        *   `configure_file_watcher`: Configure the file watcher to keep the index up-to-date automatically (e.g., set `debounce_seconds`).
        *   `get_file_watcher_status`: Check if the file watcher is healthy.
        *   `set_project_path`: Set the base path for indexing (usually handled automatically, but good for troubleshooting).
        *   `get_settings_info`: View current project settings.
        *   `refresh_search_tools`: Re-detect available command-line tools (ripgrep, etc.) if the environment changes.
        *   `clear_settings` / `create_temp_directory` / `check_temp_directory`: Maintenance tools for resetting the server state.

## Repomix Runner
*   **Purpose**: Use Repomix for packing the codebase and gathering context for large-scale analysis or refactoring.
*   **Usage**: Run Repomix via `run_command` (bash) when you need to understand the entire project structure or a specific module in depth.
*   **Reference**: Follow prompt examples from `https://repomix.com/guide/prompt-examples`.

## Git
*   **Commits**: Write clear, descriptive commit messages.
*   **Branching**: Use feature branches for new development.
