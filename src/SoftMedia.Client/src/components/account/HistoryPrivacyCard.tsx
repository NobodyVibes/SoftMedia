import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { History, Trash2, Loader2 } from 'lucide-react';
import { accountService } from '../../services/accountService';

/**
 * R-WI-013 privacy follow-up — user-owned controls over the play-history diary:
 * a "record my history" toggle (off = plays are never logged anywhere; resume positions and
 * watched checkmarks are a separate system and keep working) and a "clear my history" button.
 * Deliberately NO anonymous-logging middle mode: in a small household it de-anonymizes
 * trivially and would only offer false comfort.
 */
export function HistoryPrivacyCard() {
    const queryClient = useQueryClient();
    const [confirmingClear, setConfirmingClear] = useState(false);

    const { data: prefs } = useQuery({
        queryKey: ['historyPreferences'],
        queryFn: accountService.getHistoryPreferences,
    });

    const toggleMutation = useMutation({
        mutationFn: (enabled: boolean) => accountService.setHistoryPreferences(enabled),
        onSuccess: (_, enabled) => {
            queryClient.invalidateQueries({ queryKey: ['historyPreferences'] });
            toast.success(enabled
                ? 'Watch & listen history is now being recorded.'
                : 'History recording stopped. Your plays will not be logged.');
        },
        onError: () => toast.error('Failed to update history preference'),
    });

    const clearMutation = useMutation({
        mutationFn: accountService.clearHistory,
        onSuccess: ({ deleted }) => {
            setConfirmingClear(false);
            queryClient.invalidateQueries({ queryKey: ['historyPreferences'] });
            toast.success(deleted > 0
                ? `Erased ${deleted} ${deleted === 1 ? 'entry' : 'entries'} from your history.`
                : 'Your history was already empty.');
        },
        onError: () => toast.error('Failed to clear history'),
    });

    const recording = prefs?.recordPlaybackHistory ?? true;

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center gap-3 mb-4">
                <History className="w-5 h-5 text-blue-400" />
                <h2 className="text-lg font-semibold">Watch & Listen History</h2>
            </div>

            <div className="space-y-4">
                <div className="flex items-start justify-between gap-4">
                    <div>
                        <p className="text-sm font-medium text-gray-300">Record my history</p>
                        <p className="text-xs text-gray-500 mt-1">
                            Keeps a private diary of what you watch and listen to — only you can see it,
                            and it powers your recommendations. Turning it off stops logging entirely
                            (nothing is recorded anywhere). Resume positions and watched checkmarks are
                            unaffected. Everything stays on this server.
                        </p>
                    </div>
                    <button
                        type="button"
                        role="switch"
                        aria-checked={recording}
                        aria-label="Record my history"
                        disabled={!prefs || toggleMutation.isPending}
                        onClick={() => toggleMutation.mutate(!recording)}
                        className={`w-12 h-6 rounded-full transition-colors relative flex-shrink-0 disabled:opacity-50 ${recording ? 'bg-[#007AFF]' : 'bg-white/10'}`}
                    >
                        <div className={`absolute top-1 w-4 h-4 rounded-full bg-white transition-all ${recording ? 'left-7' : 'left-1'}`} />
                    </button>
                </div>

                <div className="pt-3 border-t border-white/5">
                    {confirmingClear ? (
                        <div className="flex items-center gap-3">
                            <p className="text-sm text-gray-300">Erase your entire history? This cannot be undone.</p>
                            <button
                                type="button"
                                onClick={() => clearMutation.mutate()}
                                disabled={clearMutation.isPending}
                                className="px-3 py-1.5 text-sm rounded bg-red-500/80 hover:bg-red-500 text-white transition-colors disabled:opacity-50"
                            >
                                {clearMutation.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : 'Yes, erase it'}
                            </button>
                            <button
                                type="button"
                                onClick={() => setConfirmingClear(false)}
                                className="px-3 py-1.5 text-sm rounded text-gray-300 hover:bg-gray-700 transition-colors"
                            >
                                Cancel
                            </button>
                        </div>
                    ) : (
                        <button
                            type="button"
                            onClick={() => setConfirmingClear(true)}
                            className="flex items-center gap-2 text-sm text-red-400/90 hover:text-red-400 transition-colors"
                        >
                            <Trash2 className="w-4 h-4" />
                            Clear my history
                        </button>
                    )}
                </div>
            </div>
        </div>
    );
}
