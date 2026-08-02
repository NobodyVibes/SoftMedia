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
    remoteMaxStreamBitrateKbps: 8000,
    maxStreamResolution: 1080,
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

    it('pre-fills all three current limits (QS-WI-002)', () => {
        renderModal({ user: userRow });
        const [base, remote] = screen.getAllByRole('spinbutton');
        expect(base).toHaveValue(3000);
        expect(remote).toHaveValue(8000);
        expect(screen.getByRole('combobox')).toHaveValue('1080');
    });

    it('saves the full limits trio on Save', async () => {
        renderModal({ user: userRow });

        const [base, remote] = screen.getAllByRole('spinbutton');
        fireEvent.change(base, { target: { value: '5000' } });
        fireEvent.change(remote, { target: { value: '2000' } });
        fireEvent.change(screen.getByRole('combobox'), { target: { value: '2160' } });
        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() =>
            expect(mockedUserService.updateUserStreaming).toHaveBeenCalledWith('user-1', {
                maxStreamBitrateKbps: 5000,
                remoteMaxStreamBitrateKbps: 2000,
                maxStreamResolution: 2160,
            }),
        );
    });

    it('saves zeros (unlimited/inherit)', async () => {
        renderModal({ user: userRow });

        const [base, remote] = screen.getAllByRole('spinbutton');
        fireEvent.change(base, { target: { value: '0' } });
        fireEvent.change(remote, { target: { value: '0' } });
        fireEvent.change(screen.getByRole('combobox'), { target: { value: '0' } });
        fireEvent.click(screen.getByRole('button', { name: /save/i }));

        await waitFor(() =>
            expect(mockedUserService.updateUserStreaming).toHaveBeenCalledWith('user-1', {
                maxStreamBitrateKbps: 0,
                remoteMaxStreamBitrateKbps: 0,
                maxStreamResolution: 0,
            }),
        );
    });

    it('states the override-wins semantic in the help copy', () => {
        renderModal({ user: userRow });
        expect(screen.getByText(/override the server's network caps/i)).toBeInTheDocument();
    });
});
