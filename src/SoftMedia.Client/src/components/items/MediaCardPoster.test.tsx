import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import MediaCard from './MediaCard';
import { MediaType, type MediaItem } from '../../types';
import * as audioStore from '../../store/audioStore';
import { useAuthStore } from '../../store/authStore';

// Mock dependencies the same way as the existing MediaCard test, but with
// useInView returning { inView: true } so the <img> element actually renders
// (LoadingImage gates rendering on inView).
vi.mock('react-router-dom', async () => {
    const actual = await vi.importActual('react-router-dom');
    return { ...actual, useNavigate: () => vi.fn() };
});

vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: true }),
}));

vi.mock('../../store/audioStore', () => ({
    useAudioStore: vi.fn(),
}));

describe('MediaCard poster rendering', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        (audioStore.useAudioStore as any).mockReturnValue({
            playTrack: vi.fn(),
            addToQueue: vi.fn(),
        });
        // Set a token so attachAuthToApiUrl produces a deterministic URL.
        useAuthStore.setState({ token: 'test-jwt-token' });
    });

    /**
     * Real movie posters come back from /api/v1/libraries/{id}/items as
     * `posterPath: "/api/v1/image/proxy?url=https%3A%2F%2Fm.media-amazon.com%2F..."`.
     * The MediaCard wraps that with width and access_token and renders it
     * via LoadingImage.
     *
     * If this test fails, the bug is in the URL transformation chain.
     */
    it('produces a proxy-shaped <img src> for a movie with proxy posterPath', () => {
        const item: MediaItem = {
            id: 'movie-1',
            title: 'Inception',
            type: MediaType.Movie,
            year: 2010,
            posterPath: '/api/v1/image/proxy?url=' + encodeURIComponent('https://m.media-amazon.com/images/M/MV5BMjAxMzY3.jpg'),
            genres: ['Action'],
            dateAdded: new Date().toISOString(),
            libraryId: 'lib1',
            sortTitle: 'Inception',
        };

        // Pass groupReady=true so LoadingImage marks the image visible
        // immediately (otherwise the image element is rendered with opacity-0
        // until the cascade advances).
        render(
            <MemoryRouter>
                <MediaCard item={item} libraryType="Movie" groupReady={true} />
            </MemoryRouter>
        );

        // Poster element should be in the DOM; assert its src has all three
        // expected query parameters: url= (the source), width=300 (card size),
        // access_token= (so <img> auth works).
        const imgs = document.querySelectorAll('img');
        const matching = Array.from(imgs).find(i => (i.getAttribute('src') ?? '').includes('image/proxy'));
        expect(matching, 'No <img> with image/proxy src was rendered').toBeDefined();

        const src = matching!.getAttribute('src')!;
        expect(src).toContain('/api/v1/image/proxy');
        expect(src).toContain('url=');
        expect(src).toContain('width=300');
        expect(src).toContain('access_token=test-jwt-token');
    });

    it('renders the fallback placeholder when posterPath is missing', () => {
        const item: MediaItem = {
            id: 'movie-2',
            title: 'No Poster',
            type: MediaType.Movie,
            year: 2010,
            posterPath: undefined,
            genres: ['Drama'],
            dateAdded: new Date().toISOString(),
            libraryId: 'lib1',
            sortTitle: 'No Poster',
        };
        render(
            <MemoryRouter>
                <MediaCard item={item} libraryType="Movie" groupReady={true} />
            </MemoryRouter>
        );

        // The fallback placeholder shows a "?" character.
        expect(screen.getByText('?')).toBeDefined();
    });
});
