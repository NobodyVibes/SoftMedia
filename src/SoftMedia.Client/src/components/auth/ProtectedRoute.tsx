import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuthStore } from '../../store/authStore';

export default function ProtectedRoute() {
    const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
    const logoutReason = useAuthStore((state) => state.logoutReason);
    const location = useLocation();

    if (!isAuthenticated) {
        // SR-WI-052: carry WHERE the user was (so login can return them there) and WHY
        // they landed on /login. reason is only 'expired' after a forced logout (refresh
        // token rejected) — a fresh visitor or manual logout gets no misleading notice.
        return (
            <Navigate
                to="/login"
                replace
                state={{ from: location, reason: logoutReason === 'expired' ? 'expired' : undefined }}
            />
        );
    }

    return <Outlet />;
}
