import { useEffect, useState } from 'react';
import { MonitorPlay, RefreshCw, Square, WifiOff } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { adminService, type ActiveSession } from '../../services/adminService';

function formatClock(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const s = Math.floor(seconds);
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    return h > 0
        ? `${h}:${m.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}`
        : `${m}:${sec.toString().padStart(2, '0')}`;
}

function TypeBadge({ session }: { session: ActiveSession }) {
    const styles: Record<ActiveSession['type'], string> = {
        Transcode: 'bg-orange-500/15 text-orange-300',
        Remux: 'bg-violet-500/15 text-violet-300',
        DirectPlay: 'bg-green-500/15 text-green-300',
    };
    const label = session.type === 'DirectPlay' ? 'Direct Play' : session.type;
    return (
        <span className={`inline-block px-2 py-0.5 rounded text-xs font-medium ${styles[session.type]}`}>
            {label}
        </span>
    );
}

/** Stable identity for a row across refetches (the session key). */
function rowKey(s: ActiveSession): string {
    return `${s.userId}:${s.mediaId}:${s.subtitleTrackIndex ?? ''}:${s.streamId ?? ''}:${s.type}`;
}

/**
 * R-WI-016 — admin "Now Playing" card. Polls every 15s while mounted; transcode
 * rows can be stopped (kills ffmpeg server-side and frees the user's cap slot),
 * direct-play rows are read-only by design.
 */
export function ActiveSessionsCard() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [confirmKey, setConfirmKey] = useState<string | null>(null);

    const { data: sessions = [], isLoading, isError } = useQuery<ActiveSession[]>({
        queryKey: ['adminSessions'],
        queryFn: adminService.getActiveSessions,
        refetchInterval: 15000,
    });

    // A confirm left open for a row that vanished (poll refetch) must not pre-arm
    // the destructive button for a future row with the same key.
    useEffect(() => {
        if (confirmKey && !sessions.some(s => rowKey(s) === confirmKey)) {
            setConfirmKey(null);
        }
    }, [sessions, confirmKey]);

    const terminateMutation = useMutation({
        mutationFn: (session: ActiveSession) => adminService.terminateSession(session),
        onSuccess: () => toast.success(t('Stream stopped')),
        onError: (error) => {
            // 404 = the session already ended on its own — that's the outcome the
            // admin wanted, not a failure; the refetch below clears the dead row.
            if (isAxiosError(error) && error.response?.status === 404) {
                toast.info(t('That stream had already ended'));
            } else {
                toast.error(t('Failed to stop the stream'));
            }
        },
        onSettled: () => {
            setConfirmKey(null);
            queryClient.invalidateQueries({ queryKey: ['adminSessions'] });
        },
    });

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center gap-3 mb-4">
                <MonitorPlay className="h-5 w-5 text-blue-400" />
                <h3 className="text-lg font-semibold text-white">{t('Now Playing')}</h3>
                {sessions.length > 0 && (
                    // B-11: interpolated, not concatenated — languages that put the
                    // count elsewhere in the phrase can reorder it.
                    <span className="text-xs text-gray-400">{t('{{count}} active', { count: sessions.length })}</span>
                )}
                {isError && sessions.length > 0 && (
                    <span title={t('Live data unavailable — showing the last known state')}>
                        <WifiOff size={14} className="text-amber-400/90" />
                    </span>
                )}
            </div>

            {isLoading ? (
                <div className="text-center py-6">
                    <RefreshCw className="animate-spin w-6 h-6 text-blue-400 mx-auto" />
                </div>
            ) : isError && sessions.length === 0 ? (
                // A failed fetch must not masquerade as "nothing is playing".
                <p className="inline-flex items-center gap-2 text-sm text-amber-400/90 py-2">
                    <WifiOff size={14} /> {t('Could not load sessions — retrying automatically.')}
                </p>
            ) : sessions.length === 0 ? (
                <p className="text-sm text-gray-500 py-2">{t('Nothing is playing right now.')}</p>
            ) : (
                <div className="overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="text-left text-gray-400 border-b border-white/10">
                                <th className="pb-2 font-medium">{t('User')}</th>
                                <th className="pb-2 font-medium">{t('Title')}</th>
                                <th className="pb-2 font-medium">{t('Method')}</th>
                                <th className="pb-2 font-medium">{t('Quality')}</th>
                                <th className="pb-2 font-medium w-48">{t('Progress')}</th>
                                <th className="pb-2 font-medium text-right">{t('Actions')}</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-white/5">
                            {sessions.map((s) => {
                                const key = rowKey(s);
                                const pct = s.durationSeconds > 0
                                    ? Math.min(100, (s.positionSeconds / s.durationSeconds) * 100)
                                    : 0;
                                return (
                                    <tr key={key} className="text-gray-300">
                                        <td className="py-2 font-medium text-white">{s.userName}</td>
                                        <td className="py-2">{s.mediaTitle}</td>
                                        <td className="py-2">
                                            <TypeBadge session={s} />
                                            {s.state !== 'Playing' && s.state !== 'Transcoding' && (
                                                // B-11: server state values ("Serving"/"Streaming"/
                                                // "Paused") go through t() like every other label.
                                                <div className="text-xs text-gray-500 mt-0.5">{t(s.state)}</div>
                                            )}
                                        </td>
                                        <td className="py-2 text-xs text-gray-400">
                                            {s.type === 'DirectPlay'
                                                ? t('Original')
                                                : [s.resolution, s.codec, s.maxBitrateKbps ? `${s.maxBitrateKbps} kbps` : null]
                                                    .filter(Boolean).join(' · ') || '—'}
                                        </td>
                                        <td className="py-2">
                                            <div className="flex items-center gap-2">
                                                <div className="flex-1 h-1.5 bg-white/10 rounded-full overflow-hidden">
                                                    <div className="h-full bg-[#007AFF] rounded-full" style={{ width: `${pct}%` }} />
                                                </div>
                                                <span className="text-xs text-gray-400 whitespace-nowrap">
                                                    {formatClock(s.positionSeconds)}
                                                    {s.durationSeconds > 0 && ` / ${formatClock(s.durationSeconds)}`}
                                                </span>
                                            </div>
                                        </td>
                                        <td className="py-2 text-right">
                                            {s.canTerminate && (confirmKey === key ? (
                                                <span className="inline-flex items-center gap-2">
                                                    <button
                                                        type="button"
                                                        onClick={() => terminateMutation.mutate(s)}
                                                        disabled={terminateMutation.isPending}
                                                        className="px-2.5 py-1.5 text-xs rounded bg-red-500/20 text-red-300 hover:bg-red-500/30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-400 disabled:opacity-50"
                                                    >
                                                        {t('Yes, stop it')}
                                                    </button>
                                                    <button
                                                        type="button"
                                                        onClick={() => setConfirmKey(null)}
                                                        className="px-2.5 py-1.5 text-xs rounded text-gray-400 hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                                                    >
                                                        {t('Cancel')}
                                                    </button>
                                                </span>
                                            ) : (
                                                <button
                                                    type="button"
                                                    onClick={() => setConfirmKey(key)}
                                                    aria-label={t('Stop the stream for {{name}}', { name: s.userName })}
                                                    className="inline-flex items-center gap-1 px-2.5 py-1.5 text-xs rounded text-red-300 hover:bg-red-500/15 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-400"
                                                >
                                                    <Square size={12} /> {t('Stop')}
                                                </button>
                                            ))}
                                        </td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                </div>
            )}
        </div>
    );
}
