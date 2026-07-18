import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateUserModal } from './CreateUserModal';
import { userService } from '../../services/userService';

vi.mock('../../services/userService', () => ({
    userService: { createUser: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const mockedUsers = vi.mocked(userService);

function renderModal() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <CreateUserModal isOpen={true} onClose={() => {}} />
        </QueryClientProvider>
    );
}

function fillRequiredFields() {
    fireEvent.change(screen.getByPlaceholderText('First Name'), { target: { value: 'Test' } });
    fireEvent.change(screen.getByPlaceholderText('Last Name'), { target: { value: 'User' } });
    fireEvent.change(screen.getByPlaceholderText('Enter username'), { target: { value: 'testuser' } });
    fireEvent.change(screen.getByPlaceholderText('Enter password'), { target: { value: 'Password1!' } });
}

beforeEach(() => {
    vi.clearAllMocks();
    mockedUsers.createUser.mockResolvedValue({} as never);
});

/**
 * R-WI-011 — content limits must be VISIBLE at creation and default to unrestricted
 * (maintainer decision: new users are never capped unless the admin picks a limit).
 */
describe('CreateUserModal content limits', () => {
    it('shows the three rating selectors defaulting to "No limit"', () => {
        renderModal();
        for (const label of [/movies/i, /^tv$/i, /games/i]) {
            const select = screen.getByLabelText(label) as HTMLSelectElement;
            expect(select.value).toBe(''); // "No limit"
        }
        expect(screen.getByText(/no content restrictions/i)).toBeInTheDocument();
    });

    it('creates WITHOUT contentRatings when no limits are chosen', async () => {
        renderModal();
        fillRequiredFields();
        fireEvent.click(screen.getByRole('button', { name: /create user/i }));

        await waitFor(() => expect(mockedUsers.createUser).toHaveBeenCalledTimes(1));
        const payload = mockedUsers.createUser.mock.calls[0][0];
        expect(payload).not.toHaveProperty('contentRatings'); // unrestricted default
    });

    it('sends only the chosen limits', async () => {
        renderModal();
        fillRequiredFields();
        fireEvent.change(screen.getByLabelText(/movies/i), { target: { value: 'PG' } });
        fireEvent.click(screen.getByRole('button', { name: /create user/i }));

        await waitFor(() => expect(mockedUsers.createUser).toHaveBeenCalledTimes(1));
        const payload = mockedUsers.createUser.mock.calls[0][0];
        expect(payload.contentRatings).toEqual({ Movie: 'PG' }); // TV/Game omitted, not ""
    });
});
