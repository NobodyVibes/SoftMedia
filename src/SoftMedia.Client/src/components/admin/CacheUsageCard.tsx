import { HardDrive, RefreshCw } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { adminService, type CacheAreaStats } from '../../services/adminService';

function formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.min(units.length - 1, Math.floor(Math.log2(bytes) / 10));
    const value = bytes / 2 ** (10 * i);
    return `${value >= 100 ? Math.round(value) : value.toFixed(1)} ${units[i]}`;
}

/**
 * MC-WI-007 — per-area footprint of the server's on-disk caches (artwork, thumbnails,
 * image proxy, trickplay, subtitles), so runaway growth is visible at a glance instead
 * of only from a shell. Cleanup itself lives in the daily "Image Cache Cleanup" task on
 * the Background Tasks card ("Run now" triggers it immediately).
 */
export function CacheUsageCard() {
    const { t } = useTranslation();

    const { data: stats = [], isLoading, refetch, isRefetching } = useQuery<CacheAreaStats[]>({
        queryKey: ['cacheStats'],
        queryFn: adminService.getCacheStats,
        staleTime: 60_000,
    });

    const totalFiles = stats.reduce((sum, s) => sum + s.files, 0);
    const totalBytes = stats.reduce((sum, s) => sum + s.bytes, 0);

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center justify-between mb-4">
                <div className="flex items-center gap-3">
                    <HardDrive className="h-5 w-5 text-blue-400" />
                    <h3 className="text-lg font-semibold text-white">{t('Cache Usage')}</h3>
                </div>
                <button
                    type="button"
                    onClick={() => refetch()}
                    disabled={isRefetching}
                    className="inline-flex items-center gap-1 px-2.5 py-1.5 text-xs rounded hover:bg-primary/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-primary disabled:opacity-50"
                    title={t('Refresh')}
                >
                    <RefreshCw size={14} className={isRefetching ? 'animate-spin' : undefined} /> {t('Refresh')}
                </button>
            </div>

            {isLoading ? (
                <div className="text-center py-6">
                    <RefreshCw className="animate-spin w-6 h-6 text-primary mx-auto" />
                </div>
            ) : (
                <div className="overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="text-left text-gray-400 border-b border-white/10">
                                <th className="pb-2 font-medium">{t('Area')}</th>
                                <th className="pb-2 font-medium text-right">{t('Files')}</th>
                                <th className="pb-2 font-medium text-right">{t('Size')}</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-white/5">
                            {stats.map((s) => (
                                <tr key={s.area} className="text-gray-300">
                                    <td className="py-1.5">{s.area}</td>
                                    <td className="py-1.5 text-right tabular-nums">{s.files.toLocaleString()}</td>
                                    <td className="py-1.5 text-right tabular-nums">{formatBytes(s.bytes)}</td>
                                </tr>
                            ))}
                            <tr className="text-white font-medium">
                                <td className="py-2">{t('Total')}</td>
                                <td className="py-2 text-right tabular-nums">{totalFiles.toLocaleString()}</td>
                                <td className="py-2 text-right tabular-nums">{formatBytes(totalBytes)}</td>
                            </tr>
                        </tbody>
                    </table>
                    <p className="text-xs text-gray-500 mt-3">
                        {t('Orphaned entries are reclaimed by the daily "Image Cache Cleanup" task — trigger it now from Background Tasks above.')}
                    </p>
                </div>
            )}
        </div>
    );
}
