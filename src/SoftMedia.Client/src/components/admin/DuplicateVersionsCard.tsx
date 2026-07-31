import { useState } from 'react';
import { Copy, RefreshCw, Scissors } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { adminService, type VersionGroup } from '../../services/adminService';

function formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.min(units.length - 1, Math.floor(Math.log2(bytes) / 10));
    const value = bytes / 2 ** (10 * i);
    return `${value >= 100 ? Math.round(value) : value.toFixed(1)} ${units[i]}`;
}

/**
 * DV-WI-012 — every title that exists as more than one file (quality/language variants
 * or accidental copies), with per-copy quality label, size and watched state. "Not a
 * duplicate" splits a copy into its own group — the split is remembered across rescans.
 * NOTE: deliberately uses its own query key; admin cards must never invalidate
 * ['settings'] (that silently reverts unsaved SettingsPage edits).
 */
export function DuplicateVersionsCard() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const [splittingId, setSplittingId] = useState<string | null>(null);

    const { data: groups = [], isLoading, refetch, isRefetching } = useQuery<VersionGroup[]>({
        queryKey: ['duplicateVersions'],
        queryFn: adminService.getDuplicateVersions,
        staleTime: 60_000,
    });

    const splitMutation = useMutation({
        mutationFn: (itemId: string) => adminService.splitVersion(itemId),
        onSettled: () => {
            setSplittingId(null);
            queryClient.invalidateQueries({ queryKey: ['duplicateVersions'] });
        },
    });

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center justify-between mb-4">
                <div className="flex items-center gap-3">
                    <Copy className="h-5 w-5 text-amber-400" />
                    <h3 className="text-lg font-semibold text-white">{t('Duplicate Versions')}</h3>
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
            ) : groups.length === 0 ? (
                <p className="text-sm text-gray-400">
                    {t('No duplicates found — every movie and episode maps to a single file.')}
                </p>
            ) : (
                <div className="space-y-4">
                    {groups.map((group) => (
                        <div key={group.versionGroupId} className="border border-white/10 rounded-lg p-3">
                            <div className="flex items-baseline justify-between gap-3 mb-2">
                                <span className="text-sm font-medium text-white">{group.displayTitle}</span>
                                <span className="text-xs text-gray-500 shrink-0">{group.libraryName}</span>
                            </div>
                            <ul className="space-y-1.5">
                                {group.members.map((member) => (
                                    <li key={member.id} className="flex items-center gap-3 text-sm text-gray-300">
                                        <span className="px-1.5 py-0.5 rounded bg-white/10 text-xs font-medium shrink-0">
                                            {member.label}
                                        </span>
                                        <span className="text-xs text-gray-500 truncate flex-1" title={member.path ?? undefined}>
                                            {member.path}
                                        </span>
                                        <span className="text-xs text-gray-400 tabular-nums shrink-0">{formatBytes(member.size)}</span>
                                        {member.watchedByCount > 0 && (
                                            <span className="text-xs text-green-400 shrink-0">
                                                {t('watched by {{count}}', { count: member.watchedByCount })}
                                            </span>
                                        )}
                                        <button
                                            type="button"
                                            disabled={splitMutation.isPending}
                                            onClick={() => {
                                                setSplittingId(member.id);
                                                splitMutation.mutate(member.id);
                                            }}
                                            className="inline-flex items-center gap-1 px-2 py-1 text-xs rounded hover:bg-amber-500/20 text-amber-300 disabled:opacity-50 shrink-0"
                                            title={t('Not a duplicate — give this file its own entry')}
                                        >
                                            <Scissors size={12} className={splittingId === member.id && splitMutation.isPending ? 'animate-pulse' : undefined} />
                                            {t('Not a duplicate')}
                                        </button>
                                    </li>
                                ))}
                            </ul>
                        </div>
                    ))}
                    <p className="text-xs text-gray-500">
                        {t('Copies of one title share watched state and appear once in the library. Splitting is remembered across rescans.')}
                    </p>
                </div>
            )}
        </div>
    );
}
