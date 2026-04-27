/**
 * ER-054: single source of truth for reader keyboard shortcuts.
 *
 * Both the keydown handler in BookReader and the help sheet read from this
 * array — adding or renaming a shortcut in one place updates the other. Keep
 * keys lowercase and human-readable; the handler matches on `e.key.toLowerCase()`.
 *
 * `group` lets the help sheet cluster related shortcuts without a second
 * config surface. `displayKey` is what the user sees on the help card — e.g.
 * "Shift + /" for the `?` shortcut so discoverability isn't broken by the
 * shift requirement on US keyboards.
 */
export interface ShortcutSpec {
    /** Human-readable key label shown in the help sheet. */
    displayKey: string;
    /** Short verb-first description. */
    description: string;
    /** Help-sheet grouping. */
    group: 'Navigate' | 'Find' | 'View' | 'Misc';
}

export const SHORTCUTS: ReadonlyArray<ShortcutSpec> = [
    // Navigate
    { displayKey: '←  /  →', description: 'Previous / next page (or spread)', group: 'Navigate' },
    { displayKey: 'PageUp  /  PageDown', description: 'Always previous / next — ignores RTL flip', group: 'Navigate' },
    // Find
    { displayKey: '/', description: 'Search inside the book (EPUB, PDF)', group: 'Find' },
    { displayKey: 'T', description: 'Table of Contents', group: 'Find' },
    { displayKey: 'B', description: 'Bookmark the current page', group: 'Find' },
    { displayKey: 'H', description: 'Toggle highlight mode (drag to select)', group: 'Find' },
    { displayKey: 'P', description: 'Pause / resume Listen (TTS)', group: 'Find' },
    { displayKey: '[  /  ]', description: 'Skip one sentence back / forward (TTS)', group: 'Find' },
    // View
    { displayKey: 'F', description: 'Fullscreen', group: 'View' },
    { displayKey: 'I', description: 'Immersive — hide chrome', group: 'View' },
    { displayKey: 'Z', description: 'Cycle reader theme (dark → sepia → high-contrast)', group: 'View' },
    { displayKey: '+  /  -', description: 'Font size (EPUB) or zoom (PDF, CBZ)', group: 'View' },
    // Misc
    { displayKey: 'Esc', description: 'Close the frontmost overlay, then exit reader', group: 'Misc' },
    { displayKey: 'Shift + /', description: 'Show this help', group: 'Misc' },
] as const;
