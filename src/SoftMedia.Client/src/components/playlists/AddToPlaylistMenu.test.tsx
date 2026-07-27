import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useState } from 'react';
import { AddToPlaylistMenu } from './AddToPlaylistMenu';
import { playlistService } from '../../services/playlistService';

vi.mock('../../services/playlistService', () => ({
    playlistService: {
        list: vi.fn(),
        create: vi.fn(),
        addItems: vi.fn(),
    },
}));

vi.mock('sonner', () => ({
    toast: { success: vi.fn(), error: vi.fn() },
}));

const listMock = vi.mocked(playlistService.list);
const addItemsMock = vi.mocked(playlistService.addItems);
const createMock = vi.mocked(playlistService.create);

const playlist = (id: string, name: string) => ({
    id,
    name,
    description: null,
    isPublic: false,
    isOwner: true,
    ownerUsername: 'me',
    itemCount: 3,
    createdAt: '2026-01-01',
    updatedAt: '2026-01-01',
    coverImagePaths: [],
    kind: 'Manual' as const,
    rules: null,
    coverImagePath: null,
});

const renderMenu = (ui: React.ReactElement) => {
    const client = new QueryClient({
        defaultOptions: { queries: { retry: false } },
    });
    return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
};

describe('AddToPlaylistMenu', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        addItemsMock.mockResolvedValue(undefined);
    });

    it('adds the track when a playlist row is clicked', async () => {
        listMock.mockResolvedValue([playlist('p1', 'Road Trip')]);
        const onClose = vi.fn();
        renderMenu(<AddToPlaylistMenu mediaItemIds={['t1']} onClose={onClose} />);

        fireEvent.click(await screen.findByText('Road Trip'));

        await waitFor(() => expect(addItemsMock).toHaveBeenCalledWith('p1', ['t1']));
        await waitFor(() => expect(onClose).toHaveBeenCalled());
    });

    it('labels each row with an Add affordance', async () => {
        listMock.mockResolvedValue([playlist('p1', 'Road Trip')]);
        renderMenu(<AddToPlaylistMenu mediaItemIds={['t1']} onClose={vi.fn()} />);

        await screen.findByText('Road Trip');
        expect(screen.getByText('Add')).toBeTruthy();
    });

    // First-run flow: with no playlists, the old UI showed dead gray text and
    // made the user find "New playlist…" themselves.
    it('jumps straight to the create form when the user has no playlists', async () => {
        listMock.mockResolvedValue([]);
        renderMenu(<AddToPlaylistMenu mediaItemIds={['t1']} onClose={vi.fn()} />);

        expect(await screen.findByPlaceholderText('Playlist name')).toBeTruthy();
        // No list exists, so there is no "Back" to return to.
        expect(screen.queryByText('Back')).toBeNull();
    });

    it('creates a playlist and adds the tracks in one step', async () => {
        listMock.mockResolvedValue([]);
        createMock.mockResolvedValue(playlist('new1', 'Fresh Mix'));
        const onClose = vi.fn();
        renderMenu(<AddToPlaylistMenu mediaItemIds={['t1', 't2']} onClose={onClose} />);

        fireEvent.change(await screen.findByPlaceholderText('Playlist name'), {
            target: { value: 'Fresh Mix' },
        });
        fireEvent.click(screen.getByText('Create & add'));

        await waitFor(() =>
            expect(createMock).toHaveBeenCalledWith({ name: 'Fresh Mix', isPublic: false })
        );
        await waitFor(() => expect(addItemsMock).toHaveBeenCalledWith('new1', ['t1', 't2']));
        await waitFor(() => expect(onClose).toHaveBeenCalled());
    });

    it('closes on outside pointerdown but ignores the trigger button', async () => {
        listMock.mockResolvedValue([playlist('p1', 'Road Trip')]);

        // Harness mirrors real usage: a trigger that toggles, menu as sibling.
        const Harness = () => {
            const [open, setOpen] = useState(true);
            return (
                <div>
                    <button
                        type="button"
                        data-add-to-playlist-trigger
                        onClick={() => setOpen(v => !v)}
                    >
                        trigger
                    </button>
                    <button type="button">elsewhere</button>
                    {open && (
                        <AddToPlaylistMenu mediaItemIds={['t1']} onClose={() => setOpen(false)} />
                    )}
                </div>
            );
        };
        renderMenu(<Harness />);
        await screen.findByText('Road Trip');

        // Clicking the trigger while open: pointerdown must NOT close it here —
        // the trigger's own click toggles it closed. If pointerdown also closed
        // it, the click would flip it straight back open (the old bug).
        const trigger = screen.getByText('trigger');
        fireEvent.pointerDown(trigger);
        expect(screen.queryByText('Road Trip')).toBeTruthy();
        fireEvent.click(trigger);
        expect(screen.queryByText('Road Trip')).toBeNull();

        // Reopen, then pointerdown anywhere else closes it.
        fireEvent.click(trigger);
        await screen.findByText('Road Trip');
        fireEvent.pointerDown(screen.getByText('elsewhere'));
        expect(screen.queryByText('Road Trip')).toBeNull();
    });

    // A smart playlist's tracks come from its rules and the server rejects an
    // add, so listing one here would be a row that can only ever fail.
    it('omits smart playlists, which cannot be added to', async () => {
        listMock.mockResolvedValue([
            playlist('p1', 'Road Trip'),
            { ...playlist('p2', 'Most Played'), kind: 'Smart' as const },
        ]);
        renderMenu(<AddToPlaylistMenu mediaItemIds={['t1']} onClose={vi.fn()} />);

        await screen.findByText('Road Trip');
        expect(screen.queryByText('Most Played')).toBeNull();
    });

    // With only smart playlists there is nothing addable to list, so the menu
    // should offer to create one rather than show an empty picker.
    it('drops straight into the create form when every playlist is smart', async () => {
        listMock.mockResolvedValue([
            { ...playlist('p2', 'Most Played'), kind: 'Smart' as const },
        ]);
        renderMenu(<AddToPlaylistMenu mediaItemIds={['t1']} onClose={vi.fn()} />);

        expect(await screen.findByPlaceholderText('Playlist name')).toBeTruthy();
    });
});
