import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { MonitorSmartphone, Loader2, CheckCircle, AlertCircle } from 'lucide-react';
import api from '../../services/api';

interface PendingDevice {
    code: string;
    deviceName: string | null;
    requestIp: string | null;
    createdAt: string;
}

/**
 * NR-WI-006 — Quick Connect authorize UI. The user types the code shown on a TV or
 * app, reviews the device details, and approves — linking that device to THEIR
 * account. The server feature is opt-in (EnableQuickConnect, admin setting); when
 * it's off (or the code is wrong/expired) lookups 404.
 */
export function QuickConnectCard() {
    const [code, setCode] = useState('');
    const [pending, setPending] = useState<PendingDevice | null>(null);
    const [error, setError] = useState('');
    const [authorized, setAuthorized] = useState(false);

    const normalized = code.trim().toUpperCase();

    const lookupMutation = useMutation({
        mutationFn: async () => {
            const response = await api.get<PendingDevice>(`/quickconnect/pending/${encodeURIComponent(normalized)}`);
            return response.data;
        },
        onSuccess: (device) => {
            setPending(device);
            setError('');
            setAuthorized(false);
        },
        onError: () => {
            setPending(null);
            setError('Code not found. Check the code on the device — or Quick Connect may be disabled on this server.');
        },
    });

    const authorizeMutation = useMutation({
        mutationFn: async () => {
            await api.post('/quickconnect/authorize', { code: normalized });
        },
        onSuccess: () => {
            setAuthorized(true);
            setPending(null);
            setCode('');
            setError('');
        },
        onError: () => {
            setError('Authorization failed — the code may have expired. Start again on the device.');
            setPending(null);
        },
    });

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center gap-3 mb-4">
                <MonitorSmartphone className="w-5 h-5 text-emerald-400" />
                <h2 className="text-lg font-semibold">Link a Device</h2>
            </div>

            <p className="text-sm text-gray-400 mb-4">
                Signing in on a TV or app? Enter the code it shows to link that device to your account.
            </p>

            {authorized && (
                <div className="flex items-center gap-2 mb-4 text-emerald-400 text-sm">
                    <CheckCircle className="w-4 h-4" />
                    Device linked. It will finish signing in on its own within a few seconds.
                </div>
            )}

            {error && (
                <div className="flex items-center gap-2 mb-4 text-red-400 text-sm">
                    <AlertCircle className="w-4 h-4 flex-shrink-0" />
                    {error}
                </div>
            )}

            {!pending ? (
                <form
                    className="flex gap-3"
                    onSubmit={(e) => {
                        e.preventDefault();
                        if (normalized.length >= 4) lookupMutation.mutate();
                    }}
                >
                    <input
                        type="text"
                        value={code}
                        onChange={(e) => setCode(e.target.value)}
                        placeholder="e.g. XK7P2M"
                        maxLength={8}
                        autoComplete="off"
                        aria-label="Device pairing code"
                        className="flex-1 max-w-[200px] bg-black/30 border border-white/10 rounded-lg px-4 py-2.5 text-white font-mono tracking-[0.2em] uppercase placeholder:tracking-normal placeholder:normal-case focus:outline-none focus:border-blue-500"
                    />
                    <button
                        type="submit"
                        disabled={normalized.length < 4 || lookupMutation.isPending}
                        className="px-5 py-2.5 min-h-[44px] bg-white/10 hover:bg-white/15 disabled:opacity-40 rounded-lg text-sm font-medium transition-colors"
                    >
                        {lookupMutation.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : 'Look up'}
                    </button>
                </form>
            ) : (
                <div className="space-y-4">
                    <div className="bg-black/30 border border-white/10 rounded-lg p-4 text-sm">
                        <div className="font-mono text-lg tracking-[0.2em] text-white mb-2">{pending.code}</div>
                        <div className="text-gray-300">{pending.deviceName || 'Unnamed device'}</div>
                        <div className="text-gray-500">
                            {pending.requestIp ? `From ${pending.requestIp} · ` : ''}
                            requested {new Date(pending.createdAt).toLocaleTimeString()}
                        </div>
                    </div>
                    <p className="text-xs text-amber-300/90">
                        Only approve codes you are looking at right now on your own device — the
                        device gets full access to your account.
                    </p>
                    <div className="flex gap-3">
                        <button
                            onClick={() => authorizeMutation.mutate()}
                            disabled={authorizeMutation.isPending}
                            className="px-5 py-2.5 min-h-[44px] bg-emerald-600 hover:bg-emerald-500 disabled:opacity-40 rounded-lg text-sm font-semibold transition-colors"
                        >
                            {authorizeMutation.isPending ? <Loader2 className="w-4 h-4 animate-spin" /> : 'Authorize this device'}
                        </button>
                        <button
                            onClick={() => { setPending(null); setCode(''); }}
                            className="px-5 py-2.5 min-h-[44px] bg-white/10 hover:bg-white/15 rounded-lg text-sm font-medium transition-colors"
                        >
                            Cancel
                        </button>
                    </div>
                </div>
            )}
        </div>
    );
}
