import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { motion } from 'framer-motion';
import { User, Key, Trash2, Globe, Subtitles, Volume2, Wifi, Save, Loader2, CheckCircle, AlertCircle } from 'lucide-react';
import { userPreferencesService } from '../services/userPreferencesService';
import { accountService } from '../services/accountService';
import { useLocalPreferences } from '../hooks/useLocalPreferences';
import { useAuthStore } from '../store/authStore';
import { useNavigate } from 'react-router-dom';

export default function MyAccountPage() {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const logout = useAuthStore((state) => state.logout);
    const user = useAuthStore((state) => state.user);

    // Server preferences
    const { data: serverPreferences, isLoading: isLoadingPrefs } = useQuery({
        queryKey: ['userPreferences'],
        queryFn: userPreferencesService.getPreferences,
    });

    // Local preferences
    const { preferences: localPrefs, updatePreference: updateLocalPref } = useLocalPreferences();

    // Local state for forms
    const [activeSection, setActiveSection] = useState<'profile' | 'preferences'>('preferences');
    const [passwordForm, setPasswordForm] = useState({ oldPassword: '', newPassword: '', confirmPassword: '' });
    const [passwordError, setPasswordError] = useState('');
    const [passwordSuccess, setPasswordSuccess] = useState('');
    const [deleteConfirmText, setDeleteConfirmText] = useState('');
    const [showDeleteModal, setShowDeleteModal] = useState(false);
    const [pendingServerPrefs, setPendingServerPrefs] = useState<Record<string, string>>({});

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

    const updatePrefsMutation = useMutation({
        mutationFn: (prefs: Record<string, string>) => userPreferencesService.updatePreferences(prefs),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['userPreferences'] });
            setPendingServerPrefs({});
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

    const handleServerPrefChange = (key: string, value: string) => {
        setPendingServerPrefs(prev => ({ ...prev, [key]: value }));
    };

    const saveServerPreferences = () => {
        if (Object.keys(pendingServerPrefs).length > 0) {
            updatePrefsMutation.mutate(pendingServerPrefs);
        }
    };

    const getServerPref = (key: string, defaultValue: string) => {
        return pendingServerPrefs[key] ?? serverPreferences?.[key] ?? defaultValue;
    };

    const hasPendingChanges = Object.keys(pendingServerPrefs).length > 0;

    if (isLoadingPrefs) {
        return (
            <div className="flex items-center justify-center min-h-screen">
                <Loader2 className="w-8 h-8 animate-spin text-primary" />
            </div>
        );
    }

    return (
        <div className="min-h-screen bg-gradient-to-br from-[#0a0a0a] via-[#121212] to-[#1a1a1a] p-6">
            <div className="max-w-4xl mx-auto">
                {/* Header */}
                <div className="mb-8">
                    <h1 className="text-3xl font-bold text-white mb-2">My Account</h1>
                    <p className="text-gray-400">Manage your profile and preferences</p>
                </div>

                {/* Section Tabs */}
                <div className="flex gap-2 mb-6">
                    <button
                        onClick={() => setActiveSection('preferences')}
                        className={`px-4 py-2 rounded-lg font-medium transition-all ${activeSection === 'preferences'
                            ? 'bg-primary text-white'
                            : 'bg-white/5 text-gray-400 hover:bg-white/10'
                            }`}
                    >
                        Preferences
                    </button>
                    <button
                        onClick={() => setActiveSection('profile')}
                        className={`px-4 py-2 rounded-lg font-medium transition-all ${activeSection === 'profile'
                            ? 'bg-primary text-white'
                            : 'bg-white/5 text-gray-400 hover:bg-white/10'
                            }`}
                    >
                        Profile & Security
                    </button>
                </div>

                {/* Content */}
                <motion.div
                    key={activeSection}
                    initial={{ opacity: 0, y: 10 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ duration: 0.2 }}
                >
                    {activeSection === 'preferences' && (
                        <div className="space-y-6">
                            {/* Server Preferences (Synced) */}
                            <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                                <div className="flex items-center gap-3 mb-4">
                                    <Globe className="w-5 h-5 text-primary" />
                                    <h2 className="text-lg font-semibold text-white">Language & Subtitles</h2>
                                    <span className="text-xs bg-primary/20 text-primary px-2 py-0.5 rounded-full">Synced</span>
                                </div>

                                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2">Interface Language</label>
                                        <select
                                            value={getServerPref('Language', 'en-US')}
                                            onChange={(e) => handleServerPrefChange('Language', e.target.value)}
                                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                                        >
                                            <option value="en-US">English (US)</option>
                                            <option value="en-GB">English (UK)</option>
                                            <option value="es">Spanish</option>
                                            <option value="fr">French</option>
                                            <option value="de">German</option>
                                            <option value="ja">Japanese</option>
                                        </select>
                                    </div>

                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2">Subtitle Language</label>
                                        <select
                                            value={getServerPref('SubtitleLanguage', 'en')}
                                            onChange={(e) => handleServerPrefChange('SubtitleLanguage', e.target.value)}
                                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                                        >
                                            <option value="en">English</option>
                                            <option value="es">Spanish</option>
                                            <option value="fr">French</option>
                                            <option value="de">German</option>
                                            <option value="ja">Japanese</option>
                                            <option value="off">Off</option>
                                        </select>
                                    </div>

                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2">Audio Language</label>
                                        <select
                                            value={getServerPref('AudioLanguage', 'en')}
                                            onChange={(e) => handleServerPrefChange('AudioLanguage', e.target.value)}
                                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                                        >
                                            <option value="en">English</option>
                                            <option value="es">Spanish</option>
                                            <option value="fr">French</option>
                                            <option value="de">German</option>
                                            <option value="ja">Japanese</option>
                                            <option value="original">Original</option>
                                        </select>
                                    </div>

                                    <div className="flex items-center justify-between bg-white/5 rounded-lg px-4 py-3">
                                        <div className="flex items-center gap-3">
                                            <Subtitles className="w-4 h-4 text-gray-400" />
                                            <span className="text-white text-sm">Auto-select subtitles</span>
                                        </div>
                                        <button
                                            onClick={() => handleServerPrefChange('AutoSelectSubtitle',
                                                getServerPref('AutoSelectSubtitle', 'true') === 'true' ? 'false' : 'true'
                                            )}
                                            className={`w-12 h-6 rounded-full transition-colors ${getServerPref('AutoSelectSubtitle', 'true') === 'true'
                                                ? 'bg-primary'
                                                : 'bg-white/20'
                                                }`}
                                        >
                                            <div className={`w-5 h-5 bg-white rounded-full transition-transform ${getServerPref('AutoSelectSubtitle', 'true') === 'true'
                                                ? 'translate-x-6'
                                                : 'translate-x-0.5'
                                                }`} />
                                        </button>
                                    </div>
                                </div>

                                {hasPendingChanges && (
                                    <div className="mt-4 flex justify-end">
                                        <button
                                            onClick={saveServerPreferences}
                                            disabled={updatePrefsMutation.isPending}
                                            className="flex items-center gap-2 px-4 py-2 bg-primary hover:bg-primary/80 text-white rounded-lg transition-colors disabled:opacity-50"
                                        >
                                            {updatePrefsMutation.isPending ? (
                                                <Loader2 className="w-4 h-4 animate-spin" />
                                            ) : (
                                                <Save className="w-4 h-4" />
                                            )}
                                            Save Changes
                                        </button>
                                    </div>
                                )}
                            </div>

                            {/* Local Preferences (Device-specific) */}
                            <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                                <div className="flex items-center gap-3 mb-4">
                                    <Wifi className="w-5 h-5 text-purple-400" />
                                    <h2 className="text-lg font-semibold text-white">Streaming Quality</h2>
                                    <span className="text-xs bg-purple-500/20 text-purple-400 px-2 py-0.5 rounded-full">This device</span>
                                </div>

                                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2">Default Quality</label>
                                        <select
                                            value={localPrefs.defaultStreamingQuality}
                                            onChange={(e) => updateLocalPref('defaultStreamingQuality', e.target.value)}
                                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                                        >
                                            <option value="auto">Auto</option>
                                            <option value="720p">720p</option>
                                            <option value="1080p">1080p</option>
                                            <option value="4k">4K</option>
                                            <option value="original">Original</option>
                                        </select>
                                    </div>

                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2">Max Bitrate (kbps)</label>
                                        <select
                                            value={localPrefs.maxBitrate}
                                            onChange={(e) => updateLocalPref('maxBitrate', e.target.value)}
                                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                                        >
                                            <option value="0">Unlimited</option>
                                            <option value="2000">2 Mbps</option>
                                            <option value="4000">4 Mbps</option>
                                            <option value="8000">8 Mbps</option>
                                            <option value="20000">20 Mbps</option>
                                            <option value="40000">40 Mbps</option>
                                        </select>
                                    </div>

                                    <div className="flex items-center justify-between bg-white/5 rounded-lg px-4 py-3 md:col-span-2">
                                        <div className="flex items-center gap-3">
                                            <Volume2 className="w-4 h-4 text-gray-400" />
                                            <div>
                                                <span className="text-white text-sm">Data Saver Mode</span>
                                                <p className="text-xs text-gray-500">Reduce quality on mobile data</p>
                                            </div>
                                        </div>
                                        <button
                                            onClick={() => updateLocalPref('dataSaverMode',
                                                localPrefs.dataSaverMode === 'true' ? 'false' : 'true'
                                            )}
                                            className={`w-12 h-6 rounded-full transition-colors ${localPrefs.dataSaverMode === 'true'
                                                ? 'bg-purple-500'
                                                : 'bg-white/20'
                                                }`}
                                        >
                                            <div className={`w-5 h-5 bg-white rounded-full transition-transform ${localPrefs.dataSaverMode === 'true'
                                                ? 'translate-x-6'
                                                : 'translate-x-0.5'
                                                }`} />
                                        </button>
                                    </div>
                                </div>
                            </div>
                        </div>
                    )}

                    {activeSection === 'profile' && (
                        <div className="space-y-6">
                            {/* User Info */}
                            <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                                <div className="flex items-center gap-3 mb-4">
                                    <User className="w-5 h-5 text-primary" />
                                    <h2 className="text-lg font-semibold text-white">Account Information</h2>
                                </div>
                                <div className="space-y-2">
                                    <p className="text-gray-400">Username: <span className="text-white">{user?.username}</span></p>
                                    <p className="text-gray-400">Role: <span className="text-white capitalize">{user?.role}</span></p>
                                </div>
                            </div>

                            {/* Password Change */}
                            <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                                <div className="flex items-center gap-3 mb-4">
                                    <Key className="w-5 h-5 text-primary" />
                                    <h2 className="text-lg font-semibold text-white">Change Password</h2>
                                </div>

                                <form onSubmit={handlePasswordSubmit} className="space-y-4">
                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2">Current Password</label>
                                        <input
                                            type="password"
                                            value={passwordForm.oldPassword}
                                            onChange={(e) => setPasswordForm(p => ({ ...p, oldPassword: e.target.value }))}
                                            className="w-full bg-white/5 border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary"
                                            required
                                        />
                                    </div>
                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2">New Password</label>
                                        <input
                                            type="password"
                                            value={passwordForm.newPassword}
                                            onChange={(e) => setPasswordForm(p => ({ ...p, newPassword: e.target.value }))}
                                            className="w-full bg-white/5 border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary"
                                            required
                                        />
                                    </div>
                                    <div>
                                        <label className="block text-sm text-gray-400 mb-2">Confirm New Password</label>
                                        <input
                                            type="password"
                                            value={passwordForm.confirmPassword}
                                            onChange={(e) => setPasswordForm(p => ({ ...p, confirmPassword: e.target.value }))}
                                            className="w-full bg-white/5 border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary"
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
                                        className="px-4 py-2 bg-primary hover:bg-primary/80 text-white rounded-lg transition-colors disabled:opacity-50 flex items-center gap-2"
                                    >
                                        {changePasswordMutation.isPending && <Loader2 className="w-4 h-4 animate-spin" />}
                                        Change Password
                                    </button>
                                </form>
                            </div>

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
                                    className="px-4 py-2 bg-red-500/20 hover:bg-red-500/30 text-red-400 rounded-lg transition-colors"
                                >
                                    Delete My Account
                                </button>
                            </div>
                        </div>
                    )}
                </motion.div>
            </div>

            {/* Delete Confirmation Modal */}
            {showDeleteModal && (
                <div className="fixed inset-0 bg-black/70 flex items-center justify-center z-50 p-4">
                    <div className="bg-[#1a1a1a] rounded-xl p-6 max-w-md w-full border border-white/10">
                        <h3 className="text-xl font-bold text-white mb-4">Confirm Account Deletion</h3>
                        <p className="text-gray-400 text-sm mb-4">
                            Type <span className="text-red-400 font-mono font-bold">DELETE</span> to confirm:
                        </p>
                        <input
                            type="text"
                            value={deleteConfirmText}
                            onChange={(e) => setDeleteConfirmText(e.target.value)}
                            placeholder="Type DELETE"
                            className="w-full bg-white/5 border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-red-500 mb-4"
                        />
                        <div className="flex gap-3">
                            <button
                                onClick={() => {
                                    setShowDeleteModal(false);
                                    setDeleteConfirmText('');
                                }}
                                className="flex-1 px-4 py-2 bg-white/10 hover:bg-white/20 text-white rounded-lg transition-colors"
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
