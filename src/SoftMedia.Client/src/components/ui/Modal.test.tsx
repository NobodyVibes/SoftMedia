import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { useState } from 'react';
import { Modal } from './Modal';

/**
 * SR-WI-051 — behavioral contract of the shared modal-dialog primitive.
 * Static guards in src/test/a11yGuards.test.ts ensure every modal renders
 * through this component; these tests pin what "through this component" buys.
 */

function renderModal(props: Partial<React.ComponentProps<typeof Modal>> = {}) {
    const onClose = vi.fn();
    const utils = render(
        <Modal isOpen={true} onClose={onClose} title="Test dialog" {...props}>
            <input placeholder="first field" />
            <button type="button">Middle</button>
            <button type="button">Last</button>
        </Modal>,
    );
    return { ...utils, onClose };
}

describe('Modal', () => {
    it('renders nothing when closed', () => {
        const { container } = render(
            <Modal isOpen={false} onClose={() => {}} title="Hidden">
                <p>body</p>
            </Modal>,
        );
        expect(container.textContent).toBe('');
    });

    it('exposes role="dialog" with aria-modal and the title as accessible name', () => {
        renderModal();
        const dialog = screen.getByRole('dialog', { name: 'Test dialog' });
        expect(dialog).toHaveAttribute('aria-modal', 'true');
        const labelledBy = dialog.getAttribute('aria-labelledby');
        expect(labelledBy).toBeTruthy();
        expect(document.getElementById(labelledBy!)?.textContent).toBe('Test dialog');
    });

    it('moves focus to the first focusable element on open', () => {
        renderModal();
        expect(screen.getByPlaceholderText('first field')).toHaveFocus();
    });

    it('honors initialFocusRef over the first focusable', () => {
        function Harness() {
            const ref = { current: null as HTMLButtonElement | null };
            return (
                <Modal isOpen={true} onClose={() => {}} title="T" initialFocusRef={ref}>
                    <input placeholder="skipped" />
                    <button type="button" ref={(el) => { ref.current = el; }}>Wanted</button>
                </Modal>
            );
        }
        render(<Harness />);
        expect(screen.getByRole('button', { name: 'Wanted' })).toHaveFocus();
    });

    it('closes on Escape', () => {
        const { onClose } = renderModal();
        fireEvent.keyDown(document, { key: 'Escape' });
        expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('closes on backdrop mousedown by default', () => {
        const { onClose } = renderModal();
        const backdrop = screen.getByRole('dialog').parentElement!;
        fireEvent.mouseDown(backdrop);
        expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('does NOT close on backdrop mousedown when closeOnBackdrop is false', () => {
        const { onClose } = renderModal({ closeOnBackdrop: false });
        const backdrop = screen.getByRole('dialog').parentElement!;
        fireEvent.mouseDown(backdrop);
        expect(onClose).not.toHaveBeenCalled();
    });

    it('does not treat clicks inside the panel as backdrop clicks', () => {
        const { onClose } = renderModal();
        fireEvent.mouseDown(screen.getByRole('button', { name: 'Middle' }));
        expect(onClose).not.toHaveBeenCalled();
    });

    it('traps Tab: wraps from the last focusable to the first', () => {
        renderModal();
        const dialog = screen.getByRole('dialog');
        screen.getByRole('button', { name: 'Last' }).focus();
        fireEvent.keyDown(dialog, { key: 'Tab' });
        expect(screen.getByPlaceholderText('first field')).toHaveFocus();
    });

    it('traps Shift+Tab: wraps from the first focusable to the last', () => {
        renderModal();
        const dialog = screen.getByRole('dialog');
        screen.getByPlaceholderText('first field').focus();
        fireEvent.keyDown(dialog, { key: 'Tab', shiftKey: true });
        expect(screen.getByRole('button', { name: 'Last' })).toHaveFocus();
    });

    it('returns focus to the trigger element on close', () => {
        function Harness() {
            const [open, setOpen] = useState(false);
            return (
                <>
                    <button type="button" onClick={() => setOpen(true)}>Open me</button>
                    <Modal isOpen={open} onClose={() => setOpen(false)} title="T">
                        <button type="button" onClick={() => setOpen(false)}>Dismiss</button>
                    </Modal>
                </>
            );
        }
        render(<Harness />);
        const trigger = screen.getByRole('button', { name: 'Open me' });
        trigger.focus();
        fireEvent.click(trigger);
        expect(screen.getByRole('button', { name: 'Dismiss' })).toHaveFocus();
        fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
        expect(trigger).toHaveFocus();
    });

    it('locks body scroll while open and restores it on close', () => {
        const { unmount } = renderModal();
        expect(document.body.style.overflow).toBe('hidden');
        unmount();
        expect(document.body.style.overflow).toBe('');
    });
});
