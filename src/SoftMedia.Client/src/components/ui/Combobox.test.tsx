import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { Combobox } from './Combobox';

/**
 * SR-WI-051 — WAI-ARIA combobox semantics and keyboard selection.
 * Keyboard map: ArrowDown/ArrowUp move the highlight (opening the list if
 * closed), Enter selects the highlighted option, Escape closes the list
 * without letting the key bubble to an enclosing dialog.
 */

const OPTIONS = ['Alpha', 'Beta', 'Gamma'];

function renderCombobox(value = '') {
    const onChange = vi.fn();
    const utils = render(<Combobox value={value} onChange={onChange} options={OPTIONS} placeholder="Pick" />);
    const input = screen.getByRole('combobox') as HTMLInputElement;
    return { ...utils, onChange, input };
}

describe('Combobox', () => {
    it('renders a collapsed combobox with aria-expanded=false', () => {
        const { input } = renderCombobox();
        expect(input).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });

    it('opens on focus and exposes listbox/option roles wired via aria-controls', () => {
        const { input } = renderCombobox('Beta');
        fireEvent.focus(input);

        expect(input).toHaveAttribute('aria-expanded', 'true');
        const listbox = screen.getByRole('listbox');
        expect(input.getAttribute('aria-controls')).toBe(listbox.id);

        const options = screen.getAllByRole('option');
        expect(options).toHaveLength(3);
        expect(screen.getByRole('option', { name: /beta/i })).toHaveAttribute('aria-selected', 'true');
        expect(screen.getByRole('option', { name: /alpha/i })).toHaveAttribute('aria-selected', 'false');
    });

    it('ArrowDown opens the list and highlights the first option (aria-activedescendant)', () => {
        const { input } = renderCombobox();
        fireEvent.keyDown(input, { key: 'ArrowDown' });

        expect(input).toHaveAttribute('aria-expanded', 'true');
        const first = screen.getByRole('option', { name: /alpha/i });
        expect(input.getAttribute('aria-activedescendant')).toBe(first.id);
    });

    it('ArrowDown/ArrowUp move the highlight and clamp at the ends', () => {
        const { input } = renderCombobox();
        fireEvent.keyDown(input, { key: 'ArrowDown' }); // open, highlight Alpha
        fireEvent.keyDown(input, { key: 'ArrowDown' }); // Beta
        expect(input.getAttribute('aria-activedescendant')).toBe(screen.getByRole('option', { name: /beta/i }).id);

        fireEvent.keyDown(input, { key: 'ArrowDown' }); // Gamma
        fireEvent.keyDown(input, { key: 'ArrowDown' }); // clamped at Gamma
        expect(input.getAttribute('aria-activedescendant')).toBe(screen.getByRole('option', { name: /gamma/i }).id);

        fireEvent.keyDown(input, { key: 'ArrowUp' }); // Beta
        fireEvent.keyDown(input, { key: 'ArrowUp' }); // Alpha
        fireEvent.keyDown(input, { key: 'ArrowUp' }); // clamped at Alpha
        expect(input.getAttribute('aria-activedescendant')).toBe(screen.getByRole('option', { name: /alpha/i }).id);
    });

    it('Enter selects the highlighted option and closes the list', () => {
        const { input, onChange } = renderCombobox();
        fireEvent.keyDown(input, { key: 'ArrowDown' }); // Alpha
        fireEvent.keyDown(input, { key: 'ArrowDown' }); // Beta
        fireEvent.keyDown(input, { key: 'Enter' });

        expect(onChange).toHaveBeenCalledWith('Beta');
        expect(input).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });

    it('Enter without a highlight does not select (falls through to the form)', () => {
        const { input, onChange } = renderCombobox();
        fireEvent.focus(input); // open, nothing highlighted
        fireEvent.keyDown(input, { key: 'Enter' });
        expect(onChange).not.toHaveBeenCalled();
    });

    it('Escape closes the list without selecting and consumes the key', () => {
        const { input, onChange } = renderCombobox();
        fireEvent.focus(input);
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        const documentSpy = vi.fn();
        document.addEventListener('keydown', documentSpy);
        try {
            fireEvent.keyDown(input, { key: 'Escape' });
        } finally {
            document.removeEventListener('keydown', documentSpy);
        }

        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
        expect(onChange).not.toHaveBeenCalled();
        // stopPropagation keeps the Escape from reaching document-level
        // listeners (i.e. an enclosing Modal's close handler).
        expect(documentSpy).not.toHaveBeenCalled();
    });

    it('typing filters the options and clicking one selects it', () => {
        const { input, onChange } = renderCombobox();
        fireEvent.focus(input);
        fireEvent.change(input, { target: { value: 'ga' } });

        const options = screen.getAllByRole('option');
        expect(options).toHaveLength(1);
        expect(options[0]).toHaveTextContent('Gamma');

        fireEvent.click(options[0]);
        expect(onChange).toHaveBeenCalledWith('Gamma');
    });
});
