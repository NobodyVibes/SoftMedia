import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { PlaylistCover } from './PlaylistCover';

/**
 * A playlist owns no artwork, so the cover is assembled from its tracks'. The
 * layout rules exist because a partially-filled mosaic reads as a broken image
 * rather than a design decision — anything under four covers falls back to a
 * single full-bleed sleeve.
 */
describe('PlaylistCover', () => {
    const covers = (n: number) =>
        Array.from({ length: n }, (_, i) => `/api/v1/music/album/album-${i}/cover`);

    it('renders the gradient fallback when there is no artwork', () => {
        const { container } = render(<PlaylistCover coverPaths={[]} />);

        expect(container.querySelectorAll('img')).toHaveLength(0);
        expect(container.querySelector('.bg-brand-gradient')).not.toBeNull();
    });

    it('treats a null cover list as no artwork', () => {
        const { container } = render(<PlaylistCover coverPaths={null} />);
        expect(container.querySelector('.bg-brand-gradient')).not.toBeNull();
    });

    it.each([1, 2, 3])('renders a single full-bleed cover for %i path(s)', (count) => {
        const { container } = render(<PlaylistCover coverPaths={covers(count)} />);
        expect(container.querySelectorAll('img')).toHaveLength(1);
    });

    it('renders a 2x2 mosaic once four covers are available', () => {
        const { container } = render(<PlaylistCover coverPaths={covers(4)} />);
        expect(container.querySelectorAll('img')).toHaveLength(4);
    });

    it('ignores covers past the fourth', () => {
        const { container } = render(<PlaylistCover coverPaths={covers(7)} />);
        expect(container.querySelectorAll('img')).toHaveLength(4);
    });

    // A cover that 404s must not leave a grey tile in the mosaic: the failed
    // source drops out and the layout re-derives from what actually loaded.
    it('drops a failed cover and re-lays out the remainder', () => {
        const { container } = render(<PlaylistCover coverPaths={covers(4)} />);

        fireEvent.error(container.querySelectorAll('img')[0]);

        // Three survivors is below the mosaic threshold, so it collapses to one.
        expect(container.querySelectorAll('img')).toHaveLength(1);
    });

    it('falls back to the gradient when the only cover fails', () => {
        const { container } = render(<PlaylistCover coverPaths={covers(1)} />);

        fireEvent.error(container.querySelector('img')!);

        expect(container.querySelectorAll('img')).toHaveLength(0);
        expect(container.querySelector('.bg-brand-gradient')).not.toBeNull();
    });

    // Cards appear by the dozen in a scrolling grid; the hero is the page's lead
    // image, where deferring would only delay the largest paint.
    it('defers card artwork but loads the hero eagerly', () => {
        const card = render(<PlaylistCover coverPaths={covers(4)} size="card" />);
        card.container.querySelectorAll('img').forEach(img =>
            expect(img.getAttribute('loading')).toBe('lazy'));

        const hero = render(<PlaylistCover coverPaths={covers(4)} size="hero" />);
        hero.container.querySelectorAll('img').forEach(img =>
            expect(img.getAttribute('loading')).toBe('eager'));
    });

    it('applies the caller-supplied footprint classes', () => {
        const { container } = render(
            <PlaylistCover coverPaths={[]} className="w-48 h-48 rounded-xl" />
        );

        const root = container.firstElementChild!;
        expect(root.className).toContain('w-48');
        expect(root.className).toContain('rounded-xl');
    });

    it('renders decorative artwork with no alt text for screen readers', () => {
        render(<PlaylistCover coverPaths={covers(4)} />);
        expect(screen.queryAllByRole('img')).toHaveLength(0);
    });
});
