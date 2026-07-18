import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StreamingModal } from './StreamingModal';
import { userService, type UserDto } from '../../services/userService';

vi.mock('../../services/userService', () => ({
    userService: {
        updateUserStreaming: vi.fn(),
    },
}));

vi.mock('sonner', () => ({
    toast: { success: vi.fn(), error: vi.fn() },
}));

const mockedUserService = vi.mocked(userService);

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
    maxStreamBitrateKbps: 3000,
};

function renderModal(props: { user: UserDto | null; isOpen?: boolean }) {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    const onClose = vi.fn();
    const utils = render(
        <QueryClientProvider client={queryClient}>
            <StreamingModal isOpen={props.isOpen ?? true} onClose={onClose} user={props.user} />
        </QueryClientProvider>,
    );
    return { ...utils, onClose };
}

beforeEach(() => {
    vi.clearAllMocks();
    mockedUserService.updateUserStreaming.mockResolvedValue();
});

describe('StreamingModal', () => {
    it('renders nothing when user is null', () => {
        const { container } = renderModal({ user: null });
        expect(container.textContent).toBe('');
    });

    it('pre-fills the user\'s current cap', () => {
        renderModal({ user: userRow });
        expect(screen.getByRole('spinbutton')).toHaveValue(3000);
    });

    it('saves the entered bitrate on Save', async () => {
        renderModal({ user: userRow });

        fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '5000' } });
        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() =>
            expect(mockedUserService.updateUserStreaming).toHaveBeenCalledWith('user-1', 5000),
        );
    });

    it('saves 0 (unlimited)', async () => {
        renderModal({ user: userRow });

        fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '0' } });
        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() =>
            expect(mockedUserService.updateUserStreaming).toHaveBeenCalledWith('user-1', 0),
        );
    });
});
