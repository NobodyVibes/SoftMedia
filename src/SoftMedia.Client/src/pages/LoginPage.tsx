import { useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { useAuthStore } from '../store/authStore';
import api from '../services/api';
import { Input } from '../components/ui/Input';
import { Button } from '../components/ui/Button';
import axios from 'axios';

/** Translate an axios error from /auth/* into a user-actionable message. */
function authErrorMessage(err: unknown, fallback: string): string {
    if (axios.isAxiosError(err)) {
        const status = err.response?.status;
        // Server may return either a string body or { message } JSON.
        const data = err.response?.data;
        const serverMessage = typeof data === 'string'
            ? data
            : (data && typeof data === 'object' && 'message' in data ? String((data as { message: unknown }).message) : undefined);

        if (status === 429) return 'Too many attempts. Please wait a minute and try again.';
        if (status === 401) return serverMessage || 'Invalid username or password.';
        if (status === 400) return serverMessage || 'Request rejected. Check your input and try again.';
        if (status === 403) return serverMessage || 'Action not allowed.';
        if (status === undefined) return 'Could not reach the server. Check your connection.';
        if (status >= 500) return 'Server error. Try again or contact your administrator.';
        return serverMessage || fallback;
    }
    return fallback;
}

export default function LoginPage() {
    const navigate = useNavigate();
    const login = useAuthStore((state) => state.login);

    const [showChangePasswordModal, setShowChangePasswordModal] = useState(false);
    const [newPassword, setNewPassword] = useState('');
    const [confirmPassword, setConfirmPassword] = useState('');
    const [tempToken, setTempToken] = useState('');

    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [error, setError] = useState('');
    const [isLoading, setIsLoading] = useState(false);

    // 2FA challenge step (P2-WI-005)
    const [twoFactorChallengeId, setTwoFactorChallengeId] = useState<string | null>(null);
    const [twoFactorCode, setTwoFactorCode] = useState('');

    const completeLogin = (user: any, token: string) => {
        if (user.mustChangePassword) {
            setTempToken(token);
            setShowChangePasswordModal(true);
            setIsLoading(false);
            return;
        }
        login(user, token);
        navigate('/');
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError('');
        setIsLoading(true);

        try {
            const response = await api.post('/auth/login', { username, password });

            // TOTP-enabled accounts return a challenge instead of tokens.
            if (response.data?.status === '2fa_required') {
                setTwoFactorChallengeId(response.data.challengeId);
                setIsLoading(false);
                return;
            }

            const { accessToken: token, user } = response.data;
            completeLogin(user, token);
        } catch (err: unknown) {
            console.error(err);
            setError(authErrorMessage(err, 'Invalid username or password'));
            setIsLoading(false);
        }
    };

    const handleTwoFactorSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError('');
        setIsLoading(true);
        try {
            const response = await api.post(
                `/auth/2fa?challengeId=${encodeURIComponent(twoFactorChallengeId!)}`,
                { challengeId: twoFactorChallengeId, code: twoFactorCode.trim() }
            );
            const { accessToken: token, user } = response.data;
            completeLogin(user, token);
        } catch (err: unknown) {
            console.error(err);
            setError(authErrorMessage(err, 'Invalid authentication code'));
            setIsLoading(false);
        }
    };

    const handleChangePassword = async (e: React.FormEvent) => {
        e.preventDefault();
        if (newPassword !== confirmPassword) {
            setError("Passwords do not match");
            return;
        }

        setIsLoading(true);
        try {
            // Use the temp token for this request
            await api.post('/auth/change-password', {
                oldPassword: password,
                newPassword: newPassword
            }, {
                headers: { Authorization: `Bearer ${tempToken}` }
            });

            // After successful change, log the user in fully
            // We need to re-login to get the updated user object (mustChangePassword = false)
            // Or we can just manually update the local user object if we trust the client state, 
            // but re-login is safer to ensure token claims are fresh if they included the flag.
            // For now, let's just proceed with the existing token but update the store.

            // Actually, let's just re-login to be clean
            const response = await api.post('/auth/login', { username, password: newPassword });
            const { accessToken: token, user } = response.data;

            login(user, token);
            navigate('/');

        } catch (err: unknown) {
            console.error(err);
            setError(authErrorMessage(err, 'Failed to change password'));
            setIsLoading(false);
        }
    };

    return (
        <div className="min-h-screen flex items-center justify-center relative overflow-hidden bg-background">
            {/* Background Effects */}
            <div className="absolute inset-0 bg-[radial-gradient(ellipse_at_top_right,_var(--tw-gradient-stops))] from-primary/20 via-background to-background" />
            <div className="absolute -top-40 -right-40 w-96 h-96 bg-primary/30 rounded-full blur-3xl opacity-20 animate-pulse" />
            <div className="absolute -bottom-40 -left-40 w-96 h-96 bg-secondary/30 rounded-full blur-3xl opacity-20 animate-pulse delay-1000" />

            {/* Glassmorphism Card */}
            <div className="w-full max-w-md p-8 rounded-2xl bg-white/5 backdrop-blur-xl border border-white/10 shadow-2xl relative z-10">
                <div className="text-center mb-8">
                    <h1 className="text-4xl font-bold bg-clip-text text-transparent bg-brand-gradient mb-2">
                        SoftMedia
                    </h1>
                    <h2 className="text-xl text-white font-medium">Welcome Back</h2>
                    <p className="text-sm text-gray-400 mt-2">
                        Sign in to access your personal media library
                    </p>
                </div>

                {twoFactorChallengeId ? (
                    <form className="space-y-6" onSubmit={handleTwoFactorSubmit}>
                        <div className="space-y-2">
                            <label className="text-sm font-medium text-gray-300 ml-1">Authentication code</label>
                            <Input
                                id="twoFactorCode"
                                type="text"
                                inputMode="numeric"
                                autoFocus
                                placeholder="6-digit code or recovery code"
                                value={twoFactorCode}
                                onChange={(e) => setTwoFactorCode(e.target.value)}
                                required
                                autoComplete="one-time-code"
                                className="bg-black/20 border-white/10 focus:border-primary/50 text-white placeholder:text-gray-500"
                            />
                            <p className="text-xs text-gray-500 ml-1">
                                Enter the code from your authenticator app, or one of your recovery codes.
                            </p>
                        </div>
                        {error && (
                            <div className="p-3 rounded-lg bg-red-500/10 border border-red-500/20 text-red-400 text-sm">{error}</div>
                        )}
                        <Button type="submit" disabled={isLoading} className="w-full">
                            {isLoading ? 'Verifying...' : 'Verify'}
                        </Button>
                        <button
                            type="button"
                            onClick={() => { setTwoFactorChallengeId(null); setTwoFactorCode(''); setError(''); }}
                            className="w-full text-sm text-gray-400 hover:text-white transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded"
                        >
                            Back to login
                        </button>
                    </form>
                ) : !showChangePasswordModal ? (
                    <form className="space-y-6" onSubmit={handleSubmit}>
                        <div className="space-y-4">
                            <div className="space-y-2">
                                <label className="text-sm font-medium text-gray-300 ml-1">Username</label>
                                <Input
                                    id="username"
                                    type="text"
                                    placeholder="Enter your username"
                                    value={username}
                                    onChange={(e) => setUsername(e.target.value)}
                                    required
                                    autoComplete="username"
                                    className="bg-black/20 border-white/10 focus:border-primary/50 text-white placeholder:text-gray-500"
                                />
                            </div>
                            <div className="space-y-2">
                                <label className="text-sm font-medium text-gray-300 ml-1">Password</label>
                                <Input
                                    id="password"
                                    type="password"
                                    placeholder="Enter your password"
                                    value={password}
                                    onChange={(e) => setPassword(e.target.value)}
                                    required
                                    autoComplete="current-password"
                                    className="bg-black/20 border-white/10 focus:border-primary/50 text-white placeholder:text-gray-500"
                                />
                            </div>
                        </div>

                        {error && (
                            <div className="text-red-400 text-sm text-center bg-red-500/10 p-3 rounded-lg border border-red-500/20">
                                {error}
                            </div>
                        )}

                        <Button
                            type="submit"
                            className="w-full bg-gradient-to-r from-blue-600 to-purple-600 hover:opacity-90 transition-opacity py-6 text-lg font-bold shadow-lg shadow-blue-500/20"
                            isLoading={isLoading}
                        >
                            Sign In
                        </Button>

                        <div className="text-center pt-4 border-t border-white/5">
                            <span className="text-gray-400 text-sm">Don't have an account? </span>
                            <Link to="/signup" className="text-primary hover:text-primary-light font-medium text-sm transition-colors">
                                Create Account
                            </Link>
                        </div>
                    </form>
                ) : (
                    <form className="space-y-6" onSubmit={handleChangePassword}>
                        <div className="text-center mb-4">
                            <div className="inline-flex items-center justify-center w-12 h-12 rounded-full bg-yellow-500/20 text-yellow-500 mb-3">
                                <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect width="18" height="11" x="3" y="11" rx="2" ry="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" /></svg>
                            </div>
                            <h3 className="text-lg font-semibold text-white">Change Password Required</h3>
                            <p className="text-sm text-gray-400 mt-1">
                                For security, you must change your password before proceeding.
                            </p>
                        </div>

                        <div className="space-y-4">
                            <div className="space-y-2">
                                <label className="text-sm font-medium text-gray-300 ml-1">New Password</label>
                                <Input
                                    type="password"
                                    placeholder="Enter new password"
                                    value={newPassword}
                                    onChange={(e) => setNewPassword(e.target.value)}
                                    required
                                    autoComplete="new-password"
                                    className="bg-black/20 border-white/10 focus:border-primary/50 text-white placeholder:text-gray-500"
                                />
                            </div>
                            <div className="space-y-2">
                                <label className="text-sm font-medium text-gray-300 ml-1">Confirm Password</label>
                                <Input
                                    type="password"
                                    placeholder="Confirm new password"
                                    value={confirmPassword}
                                    onChange={(e) => setConfirmPassword(e.target.value)}
                                    required
                                    autoComplete="new-password"
                                    className="bg-black/20 border-white/10 focus:border-primary/50 text-white placeholder:text-gray-500"
                                />
                            </div>
                        </div>

                        {error && (
                            <div className="text-red-400 text-sm text-center bg-red-500/10 p-3 rounded-lg border border-red-500/20">
                                {error}
                            </div>
                        )}

                        <Button
                            type="submit"
                            className="w-full bg-gradient-to-r from-yellow-600 to-orange-600 hover:opacity-90 transition-opacity py-6 text-lg font-bold shadow-lg shadow-yellow-500/20"
                            isLoading={isLoading}
                        >
                            Change Password
                        </Button>
                    </form>
                )}
            </div>
        </div>
    );
}
