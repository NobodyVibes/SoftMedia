import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import LoginPage from './LoginPage';
import api from '../services/api';
import { useAuthStore } from '../store/authStore';

vi.mock('../services/api', () => ({
    default: { get: vi.fn(), post: vi.fn() },
    API_URL: '/api/v1',
}));

const mockedApi = vi.mocked(api, true);

function renderLogin(state?: unknown) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <MemoryRouter initialEntries={[{ pathname: '/login', state }]}>
                <Routes>
                    <Route path="/login" element={<LoginPage />} />
                    <Route path="/settings" element={<div>settings-page</div>} />
                    <Route path="/" element={<div>home-page</div>} />
                </Routes>
            </MemoryRouter>
        </QueryClientProvider>
    );
}

async function submitLogin() {
    fireEvent.change(screen.getByPlaceholderText('Enter your username'), { target: { value: 'jo' } });
    fireEvent.change(screen.getByPlaceholderText('Enter your password'), { target: { value: 'pw' } });
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));
}

beforeEach(() => {
    vi.clearAllMocks();
    useAuthStore.setState({ user: null, token: null, mediaToken: null, isAuthenticated: false, logoutReason: null });
    mockedApi.get.mockResolvedValue({ data: { serverName: 'SoftMedia', loginMessage: null } }); // branding
    mockedApi.post.mockResolvedValue({
        data: { accessToken: 'tok', user: { id: 'u1', username: 'jo', role: 'User', mustChangePassword: false } },
    });
});

/** SR-WI-052 — expired sessions get an explanation, and re-login returns to where the user was. */
describe('LoginPage session-expiry handling', () => {
    it('shows the quiet expiry notice when redirected with reason "expired"', () => {
        renderLogin({ reason: 'expired', from: { pathname: '/settings' } });

        expect(screen.getByText(/your session expired — please sign in again\./i)).toBeInTheDocument();
    });

    it('shows no notice without the expired reason', () => {
        renderLogin();

        expect(screen.queryByText(/session expired/i)).not.toBeInTheDocument();
    });

    it('lands back on the origin route after re-login', async () => {
        renderLogin({ reason: 'expired', from: { pathname: '/settings' } });

        await submitLogin();

        expect(await screen.findByText('settings-page')).toBeInTheDocument();
    });

    it('falls back to home when there is no return path', async () => {
        renderLogin();

        await submitLogin();

        expect(await screen.findByText('home-page')).toBeInTheDocument();
    });
});
