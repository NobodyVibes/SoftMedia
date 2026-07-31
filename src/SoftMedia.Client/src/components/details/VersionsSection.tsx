import { Check, Layers, Play, Star } from 'lucide-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '../../store/authStore';
import { adminService } from '../../services/adminService';
import type { MediaItem, MediaVersion } from '../../types';

function formatBytes(bytes: number): string {
    if (!bytes || bytes <= 0) return '';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.min(units.length - 1, Math.floor(Math.log2(bytes) / 10));
    const value = bytes / 2 ** (10 * i);
    return `${value >= 100 ? Math.round(value) : value.toFixed(1)} ${units[i]}`;
}

/**
 * DV-WI-020 — the item's file copies (versions), primary first. Each row plays its own
 * file; admins can pin a "preferred version", which beats the computed primary rule
 * everywhere (grids, default play, the upcoming player switcher). Self-hides for items
 * without siblings, so it can mount unconditionally in the detail layout.
 */
export default function VersionsSection({ item }: { item: MediaItem }) {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const isAdmin = useAuthStore((s) => s.user?.role === 'Admin');

    const preferMutation = useMutation({
        mutationFn: ({ versionId, preferred }: { versionId: string; preferred: boolean }) =>
            adminService.setPreferredVersion(versionId, preferred),
        onSettled: () => queryClient.invalidateQueries({ queryKey: ['media', item.id] }),
    });

    const versions = item.versions;
    if (!versions || versions.length < 2) return null;

    const detail = (v: MediaVersion) =>
        [v.container?.toUpperCase(), formatBytes(v.size)].filter(Boolean).join(' · ');

    return (
        <section className="mb-8" aria-label={t('Versions')}>
            <h3 className="flex items-center gap-2 text-sm font-semibold text-gray-300 uppercase tracking-wider mb-3">
                <Layers className="w-4 h-4" aria-hidden="true" /> {t('Versions')}
            </h3>
            <ul className="space-y-2">
                {versions.map((version) => (
                    <li
                        key={version.id}
                        className="flex items-center gap-3 bg-white/5 border border-white/10 rounded-lg px-3 py-2"
                    >
                        <span className="px-2 py-0.5 rounded bg-white/10 text-xs font-semibold text-white shrink-0">
                            {version.label}
                        </span>
                        {version.isPrimary && (
                            <span className="text-[10px] uppercase tracking-wider text-blue-300 shrink-0">
                                {t('Default')}
                            </span>
                        )}
                        <span className="text-xs text-gray-400 truncate flex-1">{detail(version)}</span>
                        {version.watched && (
                            <span className="inline-flex items-center gap-1 text-xs text-green-400 shrink-0">
                                <Check className="w-3.5 h-3.5" aria-hidden="true" /> {t('Watched')}
                            </span>
                        )}
                        {isAdmin && (
                            <button
                                type="button"
                                onClick={() => preferMutation.mutate({ versionId: version.id, preferred: !version.preferred })}
                                disabled={preferMutation.isPending}
                                className={`p-1.5 rounded hover:bg-white/10 disabled:opacity-50 shrink-0 ${version.preferred ? 'text-amber-300' : 'text-gray-500'}`}
                                title={version.preferred ? t('Clear preferred version') : t('Prefer this version')}
                                aria-pressed={version.preferred}
                            >
                                <Star className={`w-4 h-4 ${version.preferred ? 'fill-amber-300' : ''}`} aria-hidden="true" />
                            </button>
                        )}
                        <button
                            type="button"
                            onClick={() => navigate(`/play/${version.id}`)}
                            className="inline-flex items-center gap-1.5 px-2.5 py-1.5 text-xs font-medium rounded bg-white/10 hover:bg-white/20 text-white shrink-0"
                            aria-label={t('Play {{label}} version', { label: version.label })}
                        >
                            <Play className="w-3.5 h-3.5" aria-hidden="true" /> {t('Play')}
                        </button>
                    </li>
                ))}
            </ul>
        </section>
    );
}
