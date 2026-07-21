import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { motion } from 'framer-motion';
import { User, Key, Trash2, Loader2, CheckCircle, AlertCircle, Shield } from 'lucide-react';
import { accountService, type ContentLimitsDto } from '../services/accountService';
import { useAuthStore } from '../store/authStore';
import { useNavigate } from 'react-router-dom';
import { ApiTokensCard } from '../components/account/ApiTokensCard';
import { HistoryPrivacyCard } from '../components/account/HistoryPrivacyCard';
import { TotpCard } from '../components/account/TotpCard';
import { WebhooksCard } from '../components/account/WebhooksCard';
import { QuickConnectCard } from '../components/account/QuickConnectCard';

export default function MyAccountPage() {
    const navigate = useNavigate();
    const logout = useAuthStore((state) => state.logout);
    const user = useAuthStore((state) => state.user);

    // R-WI-011: effective content limits, fetched fresh (computed server-side with the same
    // logic enforcement uses) so an admin edit shows here without re-login.
    const { data: contentLimits } = useQuery<ContentLimitsDto>({
        queryKey: ['contentLimits'],
        queryFn: accountService.getContentLimits,
    });

    // Local state for forms
    const [passwordForm, setPasswordForm] = useState({ oldPassword: '', newPassword: '', confirmPassword: '' });
    const [passwordError, setPasswordError] = useState('');
    const [passwordSuccess, setPasswordSuccess] = useState('');
    const [deleteConfirmText, setDeleteConfirmText] = useState('');
    const [showDeleteModal, setShowDeleteModal] = useState(false);

    // Mutations
    const changePasswordMutation = useMutation({
        mutationFn: () => accountService.changePassword(passwordForm.oldPassword, passwordForm.newPassword),
        onSuccess: () => {
            setPasswordSuccess('Password changed successfully!');
            setPasswordError('');
            setPasswordForm({ oldPassword: '', newPassword: '', confirmPassword: '' });
            setTimeout(() => setPasswordSuccess(''), 3000);
        },
        onError: (error: unknown) => {
            const message = error instanceof Error ? error.message : 'Failed to change password';
            setPasswordError(message);
            setPasswordSuccess('');
        },
    });

    const deleteAccountMutation = useMutation({
        mutationFn: accountService.deleteAccount,
        onSuccess: () => {
            logout();
            navigate('/login');
        },
    });

    const handlePasswordSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setPasswordError('');

        if (passwordForm.newPassword !== passwordForm.confirmPassword) {
            setPasswordError('New passwords do not match');
            return;
        }
        if (passwordForm.newPassword.length < 8) {
            setPasswordError('Password must be at least 8 characters');
            return;
        }
        if (!/[A-Z]/.test(passwordForm.newPassword) || !/[a-z]/.test(passwordForm.newPassword) || !/[0-9]/.test(passwordForm.newPassword)) {
            setPasswordError('Password must contain uppercase, lowercase, and a number');
            return;
        }

        changePasswordMutation.mutate();
    };

    const handleDeleteAccount = () => {
        if (deleteConfirmText === 'DELETE') {
            deleteAccountMutation.mutate();
        }
    };

    return (
        <div className="min-h-screen bg-gradient-to-br from-[#0a0a0a] via-[#121212] to-[#1a1a1a] p-6 text-white">
            <div className="max-w-4xl mx-auto">
                {/* Header */}
                <div className="mb-8">
                    <h1 className="text-3xl font-bold mb-2">My Account</h1>
                    <p className="text-gray-400">Manage your profile and security settings</p>
                </div>

                <motion.div
                    initial={{ opacity: 0, y: 10 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ duration: 0.2 }}
                    className="space-y-6"
                >
                    {/* User Info */}
                    <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                        <div className="flex items-center gap-3 mb-4">
                            <User className="w-5 h-5 text-primary" />
                            <h2 className="text-lg font-semibold">Account Information</h2>
                        </div>
                        <div className="space-y-2">
                            <p className="text-gray-400">Username: <span className="text-white">{user?.username}</span></p>
                            <p className="text-gray-400">Role: <span className="text-white capitalize">{user?.role}</span></p>
                        </div>

                        {/* R-WI-011: make the (previously invisible) content ceiling visible to its user. */}
                        {contentLimits && (
                            <div className="mt-4 pt-4 border-t border-white/5">
                                <div className="flex items-center gap-2 mb-1">
                                    <Shield className="w-4 h-4 text-gray-400" />
                                    <span className="text-sm font-medium text-gray-300">Content limits</span>
                                </div>
                                {contentLimits.isAdmin || (!contentLimits.movie && !contentLimits.tv && !contentLimits.game) ? (
                                    <p className="text-sm text-gray-400">
                                        None — you have full access to the library.
                                    </p>
                                ) : (
                                    <p className="text-sm text-gray-400">
                                        {[
                                            contentLimits.movie && `Movies: up to ${contentLimits.movie}`,
                                            contentLimits.tv && `TV: up to ${contentLimits.tv}`,
                                            contentLimits.game && `Games: up to ${contentLimits.game}`,
                                        ].filter(Boolean).join(' · ')}
                                        <span className="block text-xs text-gray-500 mt-0.5">
                                            Set by your administrator. Titles above these ratings are hidden.
                                        </span>
                                    </p>
                                )}
                            </div>
                        )}
                    </div>

                    {/* R-WI-013 privacy: history recording toggle + clear */}
                    <HistoryPrivacyCard />

                    {/* Password Change */}
                    <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                        <div className="flex items-center gap-3 mb-4">
                            <Key className="w-5 h-5 text-primary" />
                            <h2 className="text-lg font-semibold">Change Password</h2>
                        </div>

                        <form onSubmit={handlePasswordSubmit} className="space-y-4">
                            <div>
                                <label className="block text-sm text-gray-400 mb-2">Current Password</label>
                                <input
                                    type="password"
                                    value={passwordForm.oldPassword}
                                    onChange={(e) => setPasswordForm(p => ({ ...p, oldPassword: e.target.value }))}
                                    className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary"
                                    required
                                />
                            </div>
                            <div>
                                <label className="block text-sm text-gray-400 mb-2">New Password</label>
                                <input
                                    type="password"
                                    value={passwordForm.newPassword}
                                    onChange={(e) => setPasswordForm(p => ({ ...p, newPassword: e.target.value }))}
                                    className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary"
                                    required
                                />
                            </div>
                            <div>
                                <label className="block text-sm text-gray-400 mb-2">Confirm New Password</label>
                                <input
                                    type="password"
                                    value={passwordForm.confirmPassword}
                                    onChange={(e) => setPasswordForm(p => ({ ...p, confirmPassword: e.target.value }))}
                                    className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary"
                                    required
                                />
                            </div>

                            {passwordError && (
                                <div className="flex items-center gap-2 text-red-400 text-sm">
                                    <AlertCircle className="w-4 h-4" />
                                    {passwordError}
                                </div>
                            )}
                            {passwordSuccess && (
                                <div className="flex items-center gap-2 text-green-400 text-sm">
                                    <CheckCircle className="w-4 h-4" />
                                    {passwordSuccess}
                                </div>
                            )}

                            <button
                                type="submit"
                                disabled={changePasswordMutation.isPending}
                                className="px-6 py-2.5 bg-primary hover:bg-primary/80 text-white rounded-lg transition-all shadow-lg shadow-primary/20 disabled:opacity-50 flex items-center gap-2"
                            >
                                {changePasswordMutation.isPending && <Loader2 className="w-4 h-4 animate-spin" />}
                                Change Password
                            </button>
                        </form>
                    </div>

                    {/* Quick Connect device pairing (NR-WI-006) */}
                    <QuickConnectCard />

                    {/* Two-Factor Authentication */}
                    <TotpCard />

                    {/* API Tokens */}
                    <ApiTokensCard />

                    {/* Webhooks */}
                    <WebhooksCard />

                    {/* Delete Account */}
                    <div className="bg-red-500/10 rounded-xl p-6 border border-red-500/20">
                        <div className="flex items-center gap-3 mb-4">
                            <Trash2 className="w-5 h-5 text-red-400" />
                            <h2 className="text-lg font-semibold text-red-400">Delete Account</h2>
                        </div>
                        <p className="text-gray-400 text-sm mb-4">
                            This action cannot be undone. All your data will be permanently removed.
                        </p>
                        <button
                            onClick={() => setShowDeleteModal(true)}
                            className="px-4 py-2 bg-red-500/20 hover:bg-red-500/30 text-red-400 rounded-lg transition-colors border border-red-500/10"
                        >
                            Delete My Account
                        </button>
                    </div>
                </motion.div>
            </div>

            {/* Delete Confirmation Modal */}
            {showDeleteModal && (
                <div className="fixed inset-0 bg-black/80 backdrop-blur-sm flex items-center justify-center z-50 p-4">
                    <div className="bg-[#1a1a1a] rounded-xl p-6 max-w-md w-full border border-white/10 shadow-2xl">
                        <h3 className="text-xl font-bold text-white mb-4">Confirm Account Deletion</h3>
                        <p className="text-gray-400 text-sm mb-6">
                            Type <span className="text-red-400 font-mono font-bold">DELETE</span> to confirm:
                        </p>
                        <input
                            type="text"
                            value={deleteConfirmText}
                            onChange={(e) => setDeleteConfirmText(e.target.value)}
                            placeholder="Type DELETE"
                            className="w-full bg-white/5 border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-red-500 mb-6"
                        />
                        <div className="flex gap-3">
                            <button
                                onClick={() => {
                                    setShowDeleteModal(false);
                                    setDeleteConfirmText('');
                                }}
                                className="flex-1 px-4 py-2 bg-white/5 hover:bg-white/10 text-white rounded-lg transition-colors border border-white/5"
                            >
                                Cancel
                            </button>
                            <button
                                onClick={handleDeleteAccount}
                                disabled={deleteConfirmText !== 'DELETE' || deleteAccountMutation.isPending}
                                className="flex-1 px-4 py-2 bg-red-500 hover:bg-red-600 text-white rounded-lg transition-colors disabled:opacity-50 disabled:cursor-not-allowed flex items-center justify-center gap-2"
                            >
                                {deleteAccountMutation.isPending && <Loader2 className="w-4 h-4 animate-spin" />}
                                Delete
                            </button>
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
}
