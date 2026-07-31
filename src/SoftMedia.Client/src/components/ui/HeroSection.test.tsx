import { render, screen } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import HeroSection from './HeroSection';
import { MediaType, type MediaItem } from '../../types';
import { useAuthStore } from '../../store/authStore';

const baseItem = (over: Partial<MediaItem>): MediaItem => ({
    id: 'id-1',
    title: 'Title',
    type: MediaType.Movie,
    genres: [],
    dateAdded: new Date().toISOString(),
    libraryId: 'lib1',
    sortTitle: 'Title',
    ...over,
} as MediaItem);

const proxy = (url: string) => `/api/v1/image/proxy?url=${encodeURIComponent(url)}`;

describe('HeroSection artwork URLs', () => {
    beforeEach(() => {
        useAuthStore.setState({ mediaToken: 'test-jwt-token' });
    });

    /**
     * Only series art survived here before: their posters are downloaded to a
     * static `/cache/*` path that needs no auth. Everything else (movie
     * backdrops via the image proxy, album covers via /api/v1/music) is an
     * authenticated endpoint, and an <img>/background-image load cannot send an
     * Authorization header — the URL must carry `access_token`.
     */
    it('attaches the media token to a movie backdrop', () => {
        const { container } = render(
            <HeroSection items={[baseItem({
                type: MediaType.Movie,
                backdropPath: proxy('https://static.tvmaze.com/backdrop.jpg'),
                posterPath: '/cache/images/movie.jpg',
            })]} />
        );

        const bg = container.querySelector('[style*="background-image"]') as HTMLElement;
        expect(bg.style.backgroundImage).toContain('access_token=test-jwt-token');
    });

    it('attaches the media token to an album cover poster card', () => {
        render(
            <HeroSection items={[baseItem({
                id: 'album-1',
                title: 'Kind of Blue',
                type: MediaType.Album,
                posterPath: '/api/v1/music/album/album-1/cover',
            })]} />
        );

        const img = screen.getByAltText('Kind of Blue') as HTMLImageElement;
        expect(img.getAttribute('src')).toContain('access_token=test-jwt-token');
    });

    // AA-WI-001/004: /cache/images statics are token-gated — the hero must attach
    // the media token to a local series poster (path preserved, token appended).
    it('tokenizes a local /cache series poster without rewriting the path', () => {
        render(
            <HeroSection items={[baseItem({
                title: 'Breaking Bad',
                type: MediaType.Series,
                posterPath: '/cache/images/series/bb.jpg',
            })]} />
        );

        const img = screen.getByAltText('Breaking Bad') as HTMLImageElement;
        expect(img.getAttribute('src')).toContain('/cache/images/series/bb.jpg');
        expect(img.getAttribute('src')).toContain('access_token=test-jwt-token');
        expect(img.getAttribute('src')).not.toContain('/api/v1/cache');
    });

    it('frames music art square', () => {
        const { container } = render(
            <HeroSection items={[baseItem({
                id: 'album-1',
                title: 'Kind of Blue',
                type: MediaType.Album,
                posterPath: '/cache/images/cover.jpg',
            })]} />
        );
        expect(container.querySelector('.aspect-square')).not.toBeNull();
        expect(container.querySelector('.aspect-\\[2\\/3\\]')).toBeNull();
    });

    it('keeps non-music art 2:3', () => {
        const { container } = render(
            <HeroSection items={[baseItem({
                id: 'book-1',
                title: 'Dune',
                type: MediaType.Book,
                posterPath: '/cache/images/dune.jpg',
            })]} />
        );
        expect(container.querySelector('.aspect-\\[2\\/3\\]')).not.toBeNull();
        expect(container.querySelector('.aspect-square')).toBeNull();
    });

    /**
     * The hooks used to live below the `isLoading` early return, so the first
     * render after the hero query resolved changed the hook count and React
     * threw "Rendered more hooks than during the previous render".
     */
    it('survives the isLoading -> loaded transition', () => {
        const items = [baseItem({ title: 'Loaded', posterPath: '/cache/images/x.jpg' })];
        const { rerender } = render(<HeroSection items={[]} isLoading />);
        rerender(<HeroSection items={items} isLoading={false} />);

        expect(screen.getByRole('heading', { name: 'Loaded' })).toBeInTheDocument();
    });

    /** A shrinking items array must not index past the end. */
    it('clamps the index when the item list shrinks', () => {
        const many = [
            baseItem({ id: 'a', title: 'A', posterPath: '/cache/a.jpg' }),
            baseItem({ id: 'b', title: 'B', posterPath: '/cache/b.jpg' }),
        ];
        const { rerender } = render(<HeroSection items={many} />);
        rerender(<HeroSection items={[many[0]]} />);

        expect(screen.getByRole('heading', { name: 'A' })).toBeInTheDocument();
    });
});
