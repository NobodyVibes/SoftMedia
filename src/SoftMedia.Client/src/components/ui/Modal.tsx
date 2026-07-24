import React, { useEffect, useId, useRef } from 'react';

/**
 * SR-WI-051 — shared modal-dialog primitive. Every centered modal in the app
 * renders through this component so dialog semantics exist in exactly one
 * place:
 *
 *   - role="dialog" + aria-modal="true" + aria-labelledby wired to the title
 *   - focus moves into the dialog on open (initialFocusRef, else the first
 *     focusable element, else the panel itself)
 *   - Tab / Shift+Tab cycle inside the dialog (focus trap)
 *   - Escape closes (skipped when an inner handler already preventDefault-ed,
 *     so nested popovers — e.g. an open Combobox listbox — can consume it)
 *   - backdrop click closes, unless closeOnBackdrop={false} (destructive
 *     confirmation flows opt out so a stray click can't dismiss them)
 *   - focus returns to the triggering element on close
 *   - body scroll is locked while open
 *
 * Visuals are deliberately parameterized (panelClassName / titleClassName)
 * so adopting modals keep their existing look pixel-for-pixel — this
 * primitive is about semantics, not redesign.
 */

const FOCUSABLE_SELECTOR = [
    'a[href]',
    'button:not([disabled])',
    'input:not([disabled]):not([type="hidden"])',
    'select:not([disabled])',
    'textarea:not([disabled])',
    '[tabindex]:not([tabindex="-1"])',
].join(', ');

function getFocusable(root: HTMLElement | null): HTMLElement[] {
    if (!root) return [];
    return Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
}

export interface ModalProps {
    isOpen: boolean;
    /** Called for every dismissal affordance: Escape, backdrop click, and by callers for Cancel/X. */
    onClose: () => void;
    /** Rendered as the dialog's <h2> and wired to aria-labelledby. */
    title: React.ReactNode;
    children: React.ReactNode;
    /** Panel (dialog surface) classes. Default matches the app's standard gray-800 card. */
    panelClassName?: string;
    /** Title <h2> classes. Default matches the pre-migration heading style. */
    titleClassName?: string;
    /** Set false for destructive-confirm flows so a stray backdrop click can't dismiss. */
    closeOnBackdrop?: boolean;
    /** Element to receive initial focus instead of the first focusable. */
    initialFocusRef?: React.RefObject<HTMLElement | null>;
}

export const Modal: React.FC<ModalProps> = ({
    isOpen,
    onClose,
    title,
    children,
    panelClassName = 'bg-gray-800 rounded-lg p-6 w-full max-w-md border border-gray-700',
    titleClassName = 'text-xl font-bold text-white mb-4',
    closeOnBackdrop = true,
    initialFocusRef,
}) => {
    const titleId = useId();
    const panelRef = useRef<HTMLDivElement>(null);

    // Initial focus on open + focus return to the trigger on close/unmount.
    useEffect(() => {
        if (!isOpen) return;
        const previouslyFocused = document.activeElement as HTMLElement | null;
        const panel = panelRef.current;
        const target = initialFocusRef?.current ?? getFocusable(panel)[0] ?? panel;
        target?.focus();
        return () => {
            if (previouslyFocused && previouslyFocused.isConnected) {
                previouslyFocused.focus();
            }
        };
    }, [isOpen, initialFocusRef]);

    // Escape closes. Bubble phase + defaultPrevented check so components inside
    // the dialog (comboboxes, nested popovers) get first claim on the key.
    useEffect(() => {
        if (!isOpen) return;
        const handler = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && !e.defaultPrevented) {
                e.stopPropagation();
                onClose();
            }
        };
        document.addEventListener('keydown', handler);
        return () => document.removeEventListener('keydown', handler);
    }, [isOpen, onClose]);

    // Body scroll lock while open.
    useEffect(() => {
        if (!isOpen) return;
        const previous = document.body.style.overflow;
        document.body.style.overflow = 'hidden';
        return () => {
            document.body.style.overflow = previous;
        };
    }, [isOpen]);

    if (!isOpen) return null;

    // Focus trap: Tab past the last focusable wraps to the first and vice versa.
    const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
        if (e.key !== 'Tab') return;
        const focusables = getFocusable(panelRef.current);
        if (focusables.length === 0) {
            e.preventDefault();
            return;
        }
        const first = focusables[0];
        const last = focusables[focusables.length - 1];
        const active = document.activeElement;
        const inside = panelRef.current?.contains(active) ?? false;
        if (e.shiftKey) {
            if (active === first || !inside) {
                e.preventDefault();
                last.focus();
            }
        } else if (active === last || !inside) {
            e.preventDefault();
            first.focus();
        }
    };

    // mousedown (not click) so a text-selection drag that starts inside the
    // panel and ends on the backdrop doesn't accidentally dismiss the dialog.
    const handleBackdropMouseDown = (e: React.MouseEvent<HTMLDivElement>) => {
        if (!closeOnBackdrop) return;
        if (e.target === e.currentTarget) onClose();
    };

    return (
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
            onMouseDown={handleBackdropMouseDown}
        >
            <div
                ref={panelRef}
                role="dialog"
                aria-modal="true"
                aria-labelledby={titleId}
                tabIndex={-1}
                onKeyDown={handleKeyDown}
                className={panelClassName}
            >
                <h2 id={titleId} className={titleClassName}>
                    {title}
                </h2>
                {children}
            </div>
        </div>
    );
};
