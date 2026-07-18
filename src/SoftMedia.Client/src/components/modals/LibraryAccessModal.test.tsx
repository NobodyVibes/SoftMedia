import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { LibraryAccessModal } from './LibraryAccessModal';
import { userService, type UserDto } from '../../services/userService';
import { libraryService } from '../../services/libraryService';
import type { Library } from '../../types';

vi.mock('../../services/userService', () => ({
    userService: {
        getUserLibraryAccess: vi.fn(),
        setUserLibraryAccess: vi.fn(),
    },
}));

vi.mock('../../services/libraryService', () => ({
    libraryService: {
        getAll: vi.fn(),
    },
}));

vi.mock('sonner', () => ({
    toast: {
        success: vi.fn(),
        error: vi.fn(),
    },
}));

const mockedUserService = vi.mocked(userService);
const mockedLibraryService = vi.mocked(libraryService);

const sampleLibraries: Library[] = [
    { id: 'lib-a', name: 'Movies', type: 'Movie', paths: ['/movies'], order: 0 },
    { id: 'lib-b', name: 'Shows', type: 'TV', paths: ['/tv'], order: 1 },
    // Use a distinct name so findByText('Songs') doesn't collide with the
    // small 'Music' type label rendered alongside library names.
    { id: 'lib-c', name: 'Songs', type: 'Music', paths: ['/music'], order: 2 },
];

const userRow: UserDto = {
    id: 'user-1',
    username: 'alice',
    role: 'User',
    maxRating: '',
    createdAt: '2026-01-01',
    isBanned: false,
    isApproved: true,
    isRejected: false,
    contentRatings: {},
    firstName: 'A',
    lastName: 'L',
    createdByAdmin: false,
    usedInviteCode: null,
    twoFactorEnabled: false,
    maxStreamBitrateKbps: 0,
};

const adminRow: UserDto = { ...userRow, id: 'admin-1', username: 'admin', role: 'Admin' };

function renderModal(props: { user: UserDto | null; isOpen?: boolean }) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const onClose = vi.fn();
    const utils = render(
        <QueryClientProvider client={queryClient}>
            <LibraryAccessModal isOpen={props.isOpen ?? true} onClose={onClose} user={props.user} />
        </QueryClientProvider>,
    );
    return { ...utils, onClose };
}

beforeEach(() => {
    vi.clearAllMocks();
    mockedLibraryService.getAll.mockResolvedValue(sampleLibraries);
    mockedUserService.getUserLibraryAccess.mockResolvedValue([]);
    mockedUserService.setUserLibraryAccess.mockResolvedValue();
});

describe('LibraryAccessModal', () => {
    it('renders nothing when user is null', () => {
        const { container } = renderModal({ user: null });
        expect(container.textContent).toBe('');
    });

    it('renders an admin-only placeholder for Admin users', async () => {
        renderModal({ user: adminRow });
        expect(await screen.findByText(/admins always have access/i)).toBeInTheDocument();
        // Library list and Save button must not render in the admin path.
        expect(screen.queryByText('Movies')).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /save/i })).not.toBeInTheDocument();
    });

    it('lists every library and shows the unrestricted hint when nothing is selected', async () => {
        renderModal({ user: userRow });

        for (const lib of sampleLibraries) {
            expect(await screen.findByText(lib.name)).toBeInTheDocument();
        }
        expect(await screen.findByText(/unrestricted/i)).toBeInTheDocument();
    });

    it('pre-checks libraries from the existing access list', async () => {
        mockedUserService.getUserLibraryAccess.mockResolvedValue(['lib-a']);
        renderModal({ user: userRow });

        const moviesRow = await screen.findByRole('checkbox', { name: /movies/i });
        await waitFor(() => expect(moviesRow.getAttribute('aria-checked')).toBe('true'));

        const showsRow = screen.getByRole('checkbox', { name: /shows/i });
        expect(showsRow.getAttribute('aria-checked')).toBe('false');
    });

    it('saves the selected library ids on Save', async () => {
        renderModal({ user: userRow });

        const moviesRow = await screen.findByRole('checkbox', { name: /movies/i });
        fireEvent.click(moviesRow);
        await waitFor(() => expect(moviesRow.getAttribute('aria-checked')).toBe('true'));

        const songsRow = screen.getByRole('checkbox', { name: /songs/i });
        fireEvent.click(songsRow);

        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() =>
            expect(mockedUserService.setUserLibraryAccess).toHaveBeenCalledWith(
                'user-1',
                expect.arrayContaining(['lib-a', 'lib-c']),
            ),
        );
    });

    it('passes an empty array when the user clears all selections', async () => {
        // Pre-seed with one selection so "Clear all" is visible and meaningful.
        mockedUserService.getUserLibraryAccess.mockResolvedValue(['lib-a']);
        renderModal({ user: userRow });

        const clearButton = await screen.findByRole('button', { name: /clear all/i });
        fireEvent.click(clearButton);

        await waitFor(() => expect(screen.queryByRole('button', { name: /clear all/i })).not.toBeInTheDocument());

        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() =>
            expect(mockedUserService.setUserLibraryAccess).toHaveBeenCalledWith('user-1', []),
        );
    });

    it('toggles a library off when clicked twice', async () => {
        renderModal({ user: userRow });

        const moviesRow = await screen.findByRole('checkbox', { name: /movies/i });
        fireEvent.click(moviesRow);
        await waitFor(() => expect(moviesRow.getAttribute('aria-checked')).toBe('true'));

        fireEvent.click(moviesRow);
        await waitFor(() => expect(moviesRow.getAttribute('aria-checked')).toBe('false'));
    });
});
