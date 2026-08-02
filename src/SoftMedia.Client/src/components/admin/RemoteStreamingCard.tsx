import React, { useState } from 'react';
import { toast } from 'sonner';
import { extractApiError } from '../../services/apiError';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Globe, Save, RefreshCw } from 'lucide-react';
import { settingsService, type AppSetting } from '../../services/settingsService';

const RESOLUTION_OPTIONS = [
    { value: 'original', label: 'No limit (original)' },
    { value: '720p', label: '720p' },
    { value: '1080p', label: '1080p' },
    { value: '4k', label: '4K' },
];

const MANAGED_KEYS = ['MaxStreamingBitrate', 'MaxStreamingBitrateLan', 'RemoteMaxResolution'];

function mbpsHint(kbps: number): string {
    return kbps === 0 ? 'Unlimited' : `≈ ${(kbps / 1000).toFixed(kbps % 1000 === 0 ? 0 : 1)} Mbps`;
}

/**
 * QS-WI-001 — the "Remote streaming" card: the single surface for the network caps that
 * already existed server-side (MaxStreamingBitrate/MaxStreamingBitrateLan) plus the new
 * RemoteMaxResolution ceiling, worded for humans. These keys are FILTERED out of the
 * generic Streaming group render so no duplicate knob exists (the P2 anti-goal).
 *
 * Save flow follows DlnaSettingsCard: init-once from the shared ['settings'] query, write
 * only this card's keys, then invalidate ['settings'] — required so the page-level "Save
 * Changes" (which PUTs the whole draft) re-syncs instead of reverting this card's save;
 * SettingsPage's mergeSettingsPreservingEdits keeps other in-progress edits intact.
 */
export const RemoteStreamingCard: React.FC = () => {
    const queryClient = useQueryClient();
    const { data: settings } = useQuery({ queryKey: ['settings'], queryFn: settingsService.getAll });

    const [wanKbps, setWanKbps] = useState(20000);
    const [lanKbps, setLanKbps] = useState(0);
    const [remoteResolution, setRemoteResolution] = useState('original');

    // Init ONCE from the first settings load; later refetches must not clobber edits
    // (react.dev: "adjusting state when props change" — seeded during render).
    const [initialized, setInitialized] = useState(false);
    if (settings && !initialized) {
        setInitialized(true);
        const get = (k: string) => settings.find(s => s.key === k)?.value ?? '';
        setWanKbps(parseInt(get('MaxStreamingBitrate'), 10) || 0);
        setLanKbps(parseInt(get('MaxStreamingBitrateLan'), 10) || 0);
        setRemoteResolution(get('RemoteMaxResolution') || 'original');
    }

    const saveMutation = useMutation({
        mutationFn: (updated: AppSetting[]) => settingsService.update(updated),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['settings'] });
            toast.success('Remote streaming limits saved.');
        },
        onError: (error: unknown) => {
            toast.error(extractApiError(error, 'Failed to save remote streaming limits'));
        },
    });

    const handleSave = () => {
        const updated: AppSetting[] = (settings ?? [])
            .filter(s => MANAGED_KEYS.includes(s.key))
            .map(s => {
                let value = s.value;
                if (s.key === 'MaxStreamingBitrate') value = String(Math.max(0, Math.floor(wanKbps || 0)));
                else if (s.key === 'MaxStreamingBitrateLan') value = String(Math.max(0, Math.floor(lanKbps || 0)));
                else if (s.key === 'RemoteMaxResolution') value = remoteResolution;
                return { ...s, value };
            });
        saveMutation.mutate(updated);
    };

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center gap-3 mb-2">
                <Globe className="h-5 w-5 text-blue-400" />
                <h3 className="text-lg font-semibold text-white">Remote streaming</h3>
            </div>
            <p className="text-xs text-gray-500 mb-5">
                Limits for people streaming from outside your home network, and an optional limit for
                home (LAN) streams. When a limit reduces someone's stream, the player's
                "Why is this playing this way?" panel names it. Per-user streaming limits
                (Settings → Users) override these for that account.
            </p>

            <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
                <div className="flex flex-col gap-2">
                    <label htmlFor="remote-wan-kbps" className="text-sm font-medium text-gray-300">
                        Remote bitrate limit (kbps)
                    </label>
                    <input
                        id="remote-wan-kbps"
                        type="number"
                        min={0}
                        step={1000}
                        value={wanKbps}
                        onChange={(e) => setWanKbps(Number(e.target.value))}
                        className="bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                    />
                    <span className="text-xs text-gray-500">{mbpsHint(wanKbps)}</span>
                </div>

                <div className="flex flex-col gap-2">
                    <label htmlFor="remote-lan-kbps" className="text-sm font-medium text-gray-300">
                        Home (LAN) bitrate limit (kbps)
                    </label>
                    <input
                        id="remote-lan-kbps"
                        type="number"
                        min={0}
                        step={1000}
                        value={lanKbps}
                        onChange={(e) => setLanKbps(Number(e.target.value))}
                        className="bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                    />
                    <span className="text-xs text-gray-500">{mbpsHint(lanKbps)}</span>
                </div>

                <div className="flex flex-col gap-2">
                    <label htmlFor="remote-max-resolution" className="text-sm font-medium text-gray-300">
                        Remote resolution limit
                    </label>
                    <select
                        id="remote-max-resolution"
                        value={remoteResolution}
                        onChange={(e) => setRemoteResolution(e.target.value)}
                        className="bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                    >
                        {RESOLUTION_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                    <span className="text-xs text-gray-500">Applies only to remote streams.</span>
                </div>
            </div>

            <p className="text-xs text-gray-500 mt-4">
                Note: devices connecting through a VPN such as Tailscale (CGNAT addresses) count as
                home-network and are not affected by the remote limits.
            </p>

            <div className="pt-4">
                <button
                    type="button"
                    onClick={handleSave}
                    disabled={saveMutation.isPending || !initialized}
                    className="flex items-center gap-2 px-4 py-2 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-colors disabled:opacity-50"
                >
                    {saveMutation.isPending ? <RefreshCw className="animate-spin" size={16} /> : <Save size={16} />}
                    Save Remote Streaming
                </button>
            </div>
        </div>
    );
};
