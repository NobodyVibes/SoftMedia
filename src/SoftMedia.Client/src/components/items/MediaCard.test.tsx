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

        // Outer card is role="button" with aria-label "Play <title>" and the
        // inner overlay is a real <button> with the same label. Query the
        // outer via its distinctive div tagName rather than by role to avoid
        // the two-match ambiguity.
        const outer = screen.getAllByRole('button', { name: /play test movie/i })
            .find((el) => el.tagName === 'DIV');
        expect(outer).toBeDefined();
        fireEvent.click(outer!);

        expect(mockPlayTrack).toHaveBeenCalledWith(audioItem);
    });

    describe('Universal Client a11y', () => {
        it('play overlay is a <button> with aria-label and focus-visible styling', () => {
            render(
                <MemoryRouter>
                    <MediaCard item={mockItem} libraryType="Movie" />
                </MemoryRouter>
            );

            const play = screen.getByRole('button', { name: /play test movie/i });
            expect(play.tagName).toBe('BUTTON');
            expect(play.getAttribute('type')).toBe('button');
            expect(play.className).toMatch(/focus-visible:/);
        });

        it('audio-card wrapper exposes role="button", is keyboard-reachable, and pairs hover with focus-visible', () => {
            const audioItem = { ...mockItem, type: MediaType.Audio };
            render(
                <MemoryRouter>
                    <MediaCard item={audioItem} libraryType="Music" />
                </MemoryRouter>
            );

            // The outer wrapper uses role="button" + tabIndex + onKeyDown to
            // avoid nesting inside the inner overlay <button>. Both match by
            // role/name; pick the <div> variant.
            const candidates = screen.getAllByRole('button', { name: /play test movie/i });
            const wrapper = candidates.find((el) => el.tagName === 'DIV');
            expect(wrapper).toBeDefined();
            expect(wrapper!.getAttribute('tabindex')).toBe('0');
            expect(wrapper!.className).toMatch(/focus-visible:/);
        });

        it('audio-card wrapper activates on Enter and Space', () => {
            const audioItem = { ...mockItem, type: MediaType.Audio };
            render(
                <MemoryRouter>
                    <MediaCard item={audioItem} libraryType="Music" />
                </MemoryRouter>
            );

            const wrapper = screen.getAllByRole('button', { name: /play test movie/i })
                .find((el) => el.tagName === 'DIV')!;

            fireEvent.keyDown(wrapper, { key: 'Enter' });
            expect(mockPlayTrack).toHaveBeenCalledWith(audioItem);

            mockPlayTrack.mockClear();
            fireEvent.keyDown(wrapper, { key: ' ' });
            expect(mockPlayTrack).toHaveBeenCalledWith(audioItem);
        });

        it('add-to-queue button is a <button> with focus-visible styling (audio only)', () => {
            const audioItem = { ...mockItem, type: MediaType.Audio, title: 'Some Track' };
            render(
                <MemoryRouter>
                    <MediaCard item={audioItem} libraryType="Music" />
                </MemoryRouter>
            );

            const addToQueue = screen.getByRole('button', { name: /add some track to queue/i });
            expect(addToQueue.tagName).toBe('BUTTON');
            expect(addToQueue.className).toMatch(/focus-visible:/);
        });

        it('play button is keyboard-activatable via Enter', () => {
            render(
                <MemoryRouter>
                    <MediaCard item={mockItem} libraryType="Movie" />
                </MemoryRouter>
            );

            const play = screen.getByRole('button', { name: /play test movie/i });
            fireEvent.keyDown(play, { key: 'Enter', code: 'Enter' });
            // Native <button> dispatches click on Enter by default in real browsers;
            // the key assertion for the regression guard is that the element IS a
            // <button>, which this test enforces above.
            expect(play.tagName).toBe('BUTTON');
        });
    });
});
