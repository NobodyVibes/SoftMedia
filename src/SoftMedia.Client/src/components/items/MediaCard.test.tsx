import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import MediaCard from './MediaCard';
import { MediaType, type MediaItem } from '../../types';
import * as audioStore from '../../store/audioStore';

// Mock dependencies
const mockNavigate = vi.fn();
vi.mock('react-router-dom', async () => {
    const actual = await vi.importActual('react-router-dom');
    return {
        ...actual,
        useNavigate: () => mockNavigate,
    };
});

// Mock intersection observer for lazy-loaded images
vi.mock('react-intersection-observer', () => ({
    useInView: () => ({ ref: vi.fn(), inView: true }),
}));

// Mock hooks
vi.mock('../../store/audioStore', () => ({
    useAudioStore: vi.fn(),
}));

describe('MediaCard', () => {
    const mockItem: MediaItem = {
        id: '1',
        title: 'Test Movie',
        type: MediaType.Movie,
        year: 2023,
        posterPath: 'http://example.com/poster.jpg',
        genres: ['Action'],
        dateAdded: new Date().toISOString(),
        libraryId: 'lib1',
        sortTitle: 'Test Movie',
    };

    const mockPlayTrack = vi.fn();
    const mockAddToQueue = vi.fn();

    beforeEach(() => {
        vi.clearAllMocks();
        (audioStore.useAudioStore as any).mockReturnValue({
            playTrack: mockPlayTrack,
            addToQueue: mockAddToQueue,
        });
    });

    it('renders movie card correctly', () => {
        render(
            <MemoryRouter>
                <MediaCard item={mockItem} libraryType="Movie" />
            </MemoryRouter>
        );

        expect(screen.getByText('Test Movie')).toBeDefined();
        expect(screen.getByText('2023')).toBeDefined();
        // Check for New badge
        expect(screen.getByText('NEW')).toBeDefined();
    });

    it('navigates to details page on click for movie', () => {
        render(
            <MemoryRouter>
                <MediaCard item={mockItem} libraryType="Movie" />
            </MemoryRouter>
        );

        const link = screen.getByRole('link');
        expect(link.getAttribute('href')).toBe('/media/1');
    });

    it('plays audio directly when type is Audio', () => {
        const audioItem = { ...mockItem, type: MediaType.Audio };
        render(
            <MemoryRouter>
                <MediaCard item={audioItem} libraryType="Music" />
            </MemoryRouter>
        );

        // MediaCard for audio wraps content in a div with onClick
        // We can find it by text or just the container.
        const cardTitle = screen.getByText('Test Movie');
        fireEvent.click(cardTitle);

        expect(mockPlayTrack).toHaveBeenCalledWith(audioItem);
    });
});
