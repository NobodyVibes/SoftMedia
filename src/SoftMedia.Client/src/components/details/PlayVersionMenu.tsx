import { useEffect, useRef, useState } from 'react';
import { Check, ChevronDown, Star } from 'lucide-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '../../store/authStore';
import { adminService } from '../../services/adminService';
import { qualityBadgeStyle } from '../ui/QualityBadge';
import type { MediaItem, MediaVersion } from '../../types';

function formatBytes(bytes: number): string {
    if (!bytes || bytes <= 0) return '';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.min(units.length - 1, Math.floor(Math.log2(bytes) / 10));
    const value = bytes / 2 ** (10 * i);
    return `${value >= 100 ? Math.round(value) : value.toFixed(1)} ${units[i]}`;
}

/**
 * DV-WI-020 (revised ×2) — the split-Play chevron: a segment that sits flush against
 * the primary Play button and opens a "play this version" menu (label chip, Default
 * marker on the computed primary, container/size, per-copy watched tick, admin
 * prefer-star). Rendered only when the title has multiple file copies — the caller
 * keeps a plain full-width Play otherwise. The main Play button remains "play the
 * default"; this menu is the explicit choice, made visible at the exact moment it
 * matters (the owner found the earlier badge-dropdown too easy to miss).
 */
export default function PlayVersionMenu({ item, disabled }: { item: MediaItem; disabled?: boolean }) {
    const { t } = useTranslation();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const isAdmin = useAuthStore((s) => s.user?.role === 'Admin');
    const [open, setOpen] = useState(false);
    const rootRef = useRef<HTMLDivElement>(null);

    // Close on outside click / Escape.
    useEffect(() => {
        if (!open) return;
        const onPointerDown = (e: MouseEvent) => {
            if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
        };
        const onKeyDown = (e: KeyboardEvent) => {
            if (e.key === 'Escape') setOpen(false);
        };
        document.addEventListener('mousedown', onPointerDown);
        document.addEventListener('keydown', onKeyDown);
        return () => {
            document.removeEventListener('mousedown', onPointerDown);
            document.removeEventListener('keydown', onKeyDown);
        };
    }, [open]);

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
        <div ref={rootRef} className="relative shrink-0">
            <button
                type="button"
                onClick={() => setOpen((o) => !o)}
                disabled={disabled}
                aria-haspopup="menu"
                aria-expanded={open}
                aria-label={t('Play a specific version')}
                title={t('{{count}} versions available', { count: versions.length })}
                className="h-full flex items-center px-3 bg-violet-600 hover:bg-violet-500 text-white rounded-r-xl border-l border-white/25 shadow-lg shadow-violet-500/40 disabled:opacity-70 disabled:cursor-wait focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
            >
                <ChevronDown className={`w-5 h-5 transition-transform ${open ? 'rotate-180' : ''}`} aria-hidden="true" />
            </button>

            {open && (
                <div
                    role="menu"
                    aria-label={t('Versions')}
                    className="absolute right-0 top-full mt-2 z-30 min-w-[300px] rounded-lg bg-black/95 border border-white/10 shadow-xl py-1"
                >
                    {versions.map((version) => (
                        <div key={version.id} className="flex items-center gap-1 px-1">
                            <button
                                type="button"
                                role="menuitem"
                                onClick={() => navigate(`/play/${version.id}`)}
                                aria-label={t('Play {{label}} version', { label: version.label })}
                                className="flex items-center gap-2.5 px-2 py-2 flex-1 min-w-0 rounded text-left hover:bg-white/10 focus-visible:bg-white/10 focus-visible:outline-none"
                            >
                                <span className={`px-1.5 py-0.5 rounded text-[11px] font-semibold shrink-0 ${qualityBadgeStyle(version.label)}`}>
                                    {version.label}
                                </span>
                                {version.isPrimary && (
                                    <span className="text-[10px] uppercase tracking-wider text-blue-300 shrink-0">{t('Default')}</span>
                                )}
                                <span className="text-xs text-gray-400 truncate flex-1">{detail(version)}</span>
                                {version.watched && (
                                    <Check className="w-3.5 h-3.5 text-green-400 shrink-0" aria-label={t('Watched')} />
                                )}
                            </button>
                            {isAdmin && (
                                <button
                                    type="button"
                                    onClick={() => preferMutation.mutate({ versionId: version.id, preferred: !version.preferred })}
                                    disabled={preferMutation.isPending}
                                    className={`p-1 rounded hover:bg-white/10 disabled:opacity-50 shrink-0 ${version.preferred ? 'text-amber-300' : 'text-gray-500'}`}
                                    title={version.preferred ? t('Clear preferred version') : t('Prefer this version')}
                                    aria-pressed={version.preferred}
                                >
                                    <Star className={`w-3.5 h-3.5 ${version.preferred ? 'fill-amber-300' : ''}`} aria-hidden="true" />
                                </button>
                            )}
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
}
