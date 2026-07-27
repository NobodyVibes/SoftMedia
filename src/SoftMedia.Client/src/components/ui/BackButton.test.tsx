import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { BackButton } from './BackButton';

/**
 * One back control for the whole app. Detail surfaces previously drew three
 * different ones — a circular icon chip on media pages, small grey text links
 * with bespoke labels on playlists and collections.
 */
describe('BackButton', () => {
    const renderAt = (to: string) =>
        render(<MemoryRouter><BackButton to={to} /></MemoryRouter>);

    it('links to the hierarchical destination it is given', () => {
        renderAt('/libraries/tunes?view=playlists');

        expect(screen.getByRole('link', { name: /Back/i }).getAttribute('href'))
            .toBe('/libraries/tunes?view=playlists');
    });

    // A link, not a button calling navigate(): keyboard, middle-click and
    // open-in-new-tab all come for free, and the target is always a plain path.
    it('is a link rather than a button', () => {
        renderAt('/media/abc');

        expect(screen.getByRole('link', { name: /Back/i })).toBeTruthy();
        expect(screen.queryByRole('button')).toBeNull();
    });

    it('always reads "Back", so the control cannot drift per page', () => {
        renderAt('/');
        expect(screen.getByRole('link').textContent).toBe('Back');
    });

    it('lets a call site adjust layout without redefining the control', () => {
        render(<MemoryRouter><BackButton to="/" className="mb-2" /></MemoryRouter>);
        expect(screen.getByRole('link').className).toContain('mb-2');
    });
});
