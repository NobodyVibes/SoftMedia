import { render, screen } from '@testing-library/react';
import { describe, it, expect, beforeEach } from 'vitest';
import { MemoryRouter, Routes, Route, useLocation } from 'react-router-dom';
import ProtectedRoute from './ProtectedRoute';
import { useAuthStore } from '../../store/authStore';

/** Stand-in for LoginPage that exposes the route state ProtectedRoute attaches. */
function LoginProbe() {
    const location = useLocation();
    const state = location.state as { from?: { pathname?: string }; reason?: string } | null;
    return (
        <div>
            <span>login-probe</span>
            <span>reason:{String(state?.reason)}</span>
            <span>from:{String(state?.from?.pathname)}</span>
        </div>
    );
}

function renderAt(path: string) {
    return render(
        <MemoryRouter initialEntries={[path]}>
            <Routes>
                <Route path="/login" element={<LoginProbe />} />
                <Route element={<ProtectedRoute />}>
                    <Route path="/settings" element={<div>protected-settings</div>} />
                </Route>
            </Routes>
        </MemoryRouter>
    );
}

beforeEach(() => {
    // Real store — reset to a known logged-out baseline between tests.
    useAuthStore.setState({ user: null, token: null, mediaToken: null, isAuthenticated: false, logoutReason: null });
});

/** SR-WI-052 — forced logout must carry a return path and a "why" to the login page. */
describe('ProtectedRoute', () => {
    it('redirects with reason "expired" and the origin location after a forced logout', () => {
        useAuthStore.getState().logout('expired');
        renderAt('/settings');

        expect(screen.getByText('login-probe')).toBeInTheDocument();
        expect(screen.getByText('reason:expired')).toBeInTheDocument();
        expect(screen.getByText('from:/settings')).toBeInTheDocument();
    });

    it('passes no reason for a plain (user-initiated) logout', () => {
        useAuthStore.getState().logout();
        renderAt('/settings');

        expect(screen.getByText('reason:undefined')).toBeInTheDocument();
        expect(screen.getByText('from:/settings')).toBeInTheDocument();
    });

    it('treats a DOM event passed by onClick={logout} as a plain logout, not an expiry', () => {
        // TopBar/Sidebar wire onClick={logout}, so the first argument is a MouseEvent.
        (useAuthStore.getState().logout as (r?: unknown) => void)({ type: 'click' });
        renderAt('/settings');

        expect(screen.getByText('reason:undefined')).toBeInTheDocument();
    });

    it('renders the protected outlet when authenticated', () => {
        useAuthStore.getState().login({ id: 'u1', username: 'jo', role: 'User' }, 'tok');
        renderAt('/settings');

        expect(screen.getByText('protected-settings')).toBeInTheDocument();
    });
});
