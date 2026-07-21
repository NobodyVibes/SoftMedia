import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Network, ScrollText, RefreshCw } from 'lucide-react';
import api from '../../services/api';
import { Combobox } from '../ui/Combobox';

interface ConnectionInfo {
    machineName: string;
    lanAddresses: string[];
    requestScheme: string;
    requestHost: string;
    publishedBaseUrl: string | null;
    apiDocsEnabled: boolean;
}

interface LogEntry {
    timestampUtc: string;
    level: string;
    category: string;
    message: string;
    exception: string | null;
}

interface LogsResponse {
    entries: LogEntry[];
    currentLevel: string;
}

const levelColor: Record<string, string> = {
    Warning: 'text-amber-400',
    Error: 'text-red-400',
    Critical: 'text-red-500 font-semibold',
};

/**
 * NR-WI-010/011 — read-only connection overview + in-memory log viewer for the
 * Server & Network settings page (admin-only page; the endpoints enforce it too).
 */
export function ServerNetworkSection() {
    const [minLevel, setMinLevel] = useState('Information');

    const { data: info } = useQuery<ConnectionInfo>({
        queryKey: ['connectionInfo'],
        queryFn: async () => (await api.get<ConnectionInfo>('/system/connection-info')).data,
    });

    const { data: logs, refetch, isFetching } = useQuery<LogsResponse>({
        queryKey: ['systemLogs', minLevel],
        queryFn: async () => (await api.get<LogsResponse>(`/system/logs?take=300&minLevel=${minLevel}`)).data,
        refetchInterval: 15000,
    });

    return (
        <div className="space-y-8 mt-8">
            <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                <div className="flex items-center gap-3 mb-4">
                    <Network className="w-5 h-5 text-blue-400" />
                    <h3 className="text-lg font-semibold">Connection</h3>
                </div>
                {info && (
                    <dl className="grid grid-cols-1 md:grid-cols-2 gap-x-8 gap-y-3 text-sm">
                        <div>
                            <dt className="text-gray-400">You are connected via</dt>
                            <dd className="text-white font-mono">{info.requestScheme}://{info.requestHost}</dd>
                        </div>
                        <div>
                            <dt className="text-gray-400">Machine</dt>
                            <dd className="text-white">{info.machineName}</dd>
                        </div>
                        <div>
                            <dt className="text-gray-400">LAN addresses</dt>
                            <dd className="text-white font-mono">{info.lanAddresses.join(', ') || '—'}</dd>
                        </div>
                        <div>
                            <dt className="text-gray-400">Published URL</dt>
                            <dd className="text-white font-mono">{info.publishedBaseUrl ?? 'not configured'}</dd>
                        </div>
                        <div className="md:col-span-2 text-gray-500">
                            Ports and HTTPS are configured at the host level (Kestrel / your reverse
                            proxy) — see the reverse-proxy guide in the docs.
                            {info.apiDocsEnabled && ' API reference is being served at /swagger.'}
                        </div>
                    </dl>
                )}
            </div>

            <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                <div className="flex flex-wrap items-center gap-3 mb-4">
                    <ScrollText className="w-5 h-5 text-purple-400" />
                    <h3 className="text-lg font-semibold flex-1">Recent Logs</h3>
                    <Combobox
                        value={minLevel}
                        onChange={setMinLevel}
                        options={['Information', 'Warning', 'Error']}
                        placeholder="Min level"
                        className="w-40"
                    />
                    <button
                        onClick={() => refetch()}
                        title="Refresh logs"
                        aria-label="Refresh logs"
                        className="p-3 min-w-[44px] min-h-[44px] flex items-center justify-center rounded-lg bg-white/5 hover:bg-white/10 transition-colors"
                    >
                        <RefreshCw className={`w-4 h-4 ${isFetching ? 'animate-spin' : ''}`} />
                    </button>
                </div>
                <p className="text-xs text-gray-500 mb-3">
                    The most recent in-memory entries (nothing is written to disk or sent
                    anywhere). Current verbosity: <span className="text-gray-300">{logs?.currentLevel}</span> —
                    change it with the Log Level setting above; it applies immediately.
                </p>
                <div className="bg-black/40 rounded-lg border border-white/5 max-h-[420px] overflow-y-auto font-mono text-xs leading-relaxed p-3">
                    {(logs?.entries ?? []).length === 0 && (
                        <div className="text-gray-500 italic">No entries at this level yet.</div>
                    )}
                    {[...(logs?.entries ?? [])].reverse().map((entry, i) => (
                        <div key={i} className="py-0.5 border-b border-white/5 last:border-0">
                            <span className="text-gray-500">{new Date(entry.timestampUtc).toLocaleTimeString()}</span>{' '}
                            <span className={levelColor[entry.level] ?? 'text-gray-300'}>{entry.level}</span>{' '}
                            <span className="text-gray-400">{entry.category.split('.').pop()}</span>{' '}
                            <span className="text-gray-200 break-all">{entry.message}</span>
                            {entry.exception && (
                                <pre className="text-red-300/80 whitespace-pre-wrap mt-1">{entry.exception}</pre>
                            )}
                        </div>
                    ))}
                </div>
            </div>
        </div>
    );
}
