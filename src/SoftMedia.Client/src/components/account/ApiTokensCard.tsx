import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { KeyRound, Plus, Trash2, Copy, Check, Loader2, X } from 'lucide-react';
import { accountService, type ApiTokenDto } from '../../services/accountService';
import { useAuthStore } from '../../store/authStore';

const ALL_SCOPES: { value: string; label: string; description: string }[] = [
    { value: 'read:library', label: 'Read library', description: 'Read media metadata and library structure' },
    { value: 'read:state', label: 'Read state', description: 'Read playback state, watchlist, playlists' },
    { value: 'write:state', label: 'Write state', description: 'Modify playback state, watchlist, playlists' },
    { value: 'write:library', label: 'Trigger scans', description: 'Trigger library scans (for Sonarr/Radarr webhooks)' },
    { value: 'admin', label: 'Admin', description: 'Full admin access (admins only)' },
];

export function ApiTokensCard() {
    const queryClient = useQueryClient();
    const isAdmin = useAuthStore((s) => s.user?.role === 'Admin');

    const [showForm, setShowForm] = useState(false);
    const [label, setLabel] = useState('');
    const [selectedScopes, setSelectedScopes] = useState<string[]>(['read:library']);
    const [mintedToken, setMintedToken] = useState<string | null>(null);
    const [copied, setCopied] = useState(false);
    const [error, setError] = useState('');

    const { data: tokens = [], isLoading } = useQuery<ApiTokenDto[]>({
        queryKey: ['apiTokens'],
        queryFn: accountService.listApiTokens,
    });

    const invalidate = () => queryClient.invalidateQueries({ queryKey: ['apiTokens'] });

    const createMutation = useMutation({
        mutationFn: () => accountService.createApiToken(label, selectedScopes, null),
        onSuccess: (res) => {
            setMintedToken(res.token);
            setShowForm(false);
            setLabel('');
            setSelectedScopes(['read:library']);
            setError('');
            invalidate();
        },
        onError: (e: unknown) => {
            setError(e instanceof Error ? e.message : 'Failed to create token');
        },
    });

    const revokeMutation = useMutation({
        mutationFn: accountService.revokeApiToken,
        onSuccess: invalidate,
    });

    const toggleScope = (scope: string) => {
        setSelectedScopes((prev) =>
            prev.includes(scope) ? prev.filter((s) => s !== scope) : [...prev, scope]
        );
    };

    const copyToken = async () => {
        if (!mintedToken) return;
        await navigator.clipboard.writeText(mintedToken);
        setCopied(true);
        setTimeout(() => setCopied(false), 2000);
    };

    const availableScopes = ALL_SCOPES.filter((s) => s.value !== 'admin' || isAdmin);

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center justify-between mb-4">
                <div className="flex items-center gap-3">
                    <KeyRound className="w-5 h-5 text-primary" />
                    <h2 className="text-lg font-semibold">API Tokens</h2>
                </div>
                {!showForm && (
                    <button
                        type="button"
                        onClick={() => setShowForm(true)}
                        className="inline-flex items-center gap-2 px-3 py-1.5 text-sm bg-primary hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg transition-colors"
                    >
                        <Plus size={16} /> New Token
                    </button>
                )}
            </div>

            <p className="text-sm text-gray-400 mb-4">
                Long-lived tokens for scripts, dashboards, and integrations. Treat them like passwords —
                a token is shown only once when created.
            </p>

            {/* One-time minted token display */}
            {mintedToken && (
                <div className="mb-4 p-4 bg-blue-500/10 border border-blue-500/30 rounded-lg">
                    <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0 flex-1">
                            <p className="text-sm text-blue-300 font-medium mb-1">
                                Copy your token now — it will not be shown again.
                            </p>
                            <code className="block text-xs font-mono text-white break-all bg-black/30 rounded px-2 py-1.5">
                                {mintedToken}
                            </code>
                        </div>
                        <div className="flex items-center gap-1 shrink-0">
                            <button
                                type="button"
                                onClick={copyToken}
                                className="p-2 rounded hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-blue-300"
                                title="Copy"
                            >
                                {copied ? <Check size={16} className="text-green-400" /> : <Copy size={16} />}
                            </button>
                            <button
                                type="button"
                                onClick={() => setMintedToken(null)}
                                className="p-2 rounded hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-gray-400"
                                title="Dismiss"
                            >
                                <X size={16} />
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* Create form */}
            {showForm && (
                <div className="mb-6 p-4 bg-black/20 border border-white/10 rounded-lg space-y-4">
                    <div>
                        <label className="block text-sm text-gray-400 mb-2">Label</label>
                        <input
                            type="text"
                            value={label}
                            onChange={(e) => setLabel(e.target.value)}
                            placeholder="e.g. Home Assistant"
                            maxLength={100}
                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary"
                        />
                    </div>
                    <div>
                        <span className="block text-sm text-gray-400 mb-2">Scopes</span>
                        <div className="space-y-2">
                            {availableScopes.map((scope) => (
                                <label key={scope.value} className="flex items-start gap-3 cursor-pointer">
                                    <input
                                        type="checkbox"
                                        checked={selectedScopes.includes(scope.value)}
                                        onChange={() => toggleScope(scope.value)}
                                        className="mt-1 accent-primary"
                                    />
                                    <span>
                                        <span className="text-sm text-white">{scope.label}</span>
                                        <span className="block text-xs text-gray-500">{scope.description}</span>
                                    </span>
                                </label>
                            ))}
                        </div>
                    </div>

                    {error && <p className="text-sm text-red-400">{error}</p>}

                    <div className="flex gap-3">
                        <button
                            type="button"
                            onClick={() => createMutation.mutate()}
                            disabled={createMutation.isPending || selectedScopes.length === 0}
                            className="inline-flex items-center gap-2 px-4 py-2 text-sm bg-primary hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg transition-colors disabled:opacity-50"
                        >
                            {createMutation.isPending && <Loader2 size={16} className="animate-spin" />}
                            Create Token
                        </button>
                        <button
                            type="button"
                            onClick={() => { setShowForm(false); setError(''); }}
                            className="px-4 py-2 text-sm bg-white/5 hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg transition-colors"
                        >
                            Cancel
                        </button>
                    </div>
                </div>
            )}

            {/* Token list */}
            {isLoading ? (
                <div className="text-center py-6">
                    <Loader2 className="animate-spin w-6 h-6 text-primary mx-auto" />
                </div>
            ) : tokens.length === 0 ? (
                <p className="text-sm text-gray-500 py-2">No API tokens yet.</p>
            ) : (
                <ul className="divide-y divide-white/5">
                    {tokens.map((token) => (
                        <li key={token.id} className="py-3 flex items-center justify-between gap-3">
                            <div className="min-w-0">
                                <p className="text-white truncate">{token.label}</p>
                                <p className="text-xs text-gray-500">
                                    {token.scopes.join(', ')}
                                    {' · '}
                                    {token.lastUsedAt
                                        ? `last used ${new Date(token.lastUsedAt).toLocaleString()}`
                                        : 'never used'}
                                </p>
                            </div>
                            <button
                                type="button"
                                onClick={() => revokeMutation.mutate(token.id)}
                                disabled={revokeMutation.isPending}
                                className="p-2 rounded hover:bg-red-500/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-gray-400 hover:text-red-400 shrink-0"
                                title="Revoke"
                            >
                                <Trash2 size={16} />
                            </button>
                        </li>
                    ))}
                </ul>
            )}
        </div>
    );
}
