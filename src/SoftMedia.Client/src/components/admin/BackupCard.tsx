import { useRef, useState } from 'react';
import axios from 'axios';
import { Database, Download, Save, Upload, Pin, PinOff, RefreshCw, Image, Trash2, Check, X, Pencil } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { adminService, type BackupInfo } from '../../services/adminService';

/** Pull the server's error text out of an axios error (string or { message } body). */
function serverErrorMessage(err: unknown): string | undefined {
    if (!axios.isAxiosError(err)) return undefined;
    const data = err.response?.data;
    if (typeof data === 'string') return data;
    if (data && typeof data === 'object' && 'message' in data) {
        return String((data as { message: unknown }).message);
    }
    return undefined;
}

/** Make a user-supplied name safe to use as a download filename. */
function toFileName(name: string): string {
    const cleaned = name.replace(/[\\/:*?"<>|]/g, '_').trim();
    return (cleaned.length > 0 ? cleaned : 'softmedia-backup') + '.zip';
}

function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    const units = ['KB', 'MB', 'GB'];
    let value = bytes / 1024;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }
    return `${value.toFixed(1)} ${units[unit]}`;
}

export function BackupCard() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();
    const fileInputRef = useRef<HTMLInputElement>(null);

    // Optional name typed before clicking Create.
    const [newName, setNewName] = useState('');
    // Inline rename state: the id being edited and its working value.
    const [editingId, setEditingId] = useState<string | null>(null);
    const [editValue, setEditValue] = useState('');

    const { data: backups = [], isLoading } = useQuery<BackupInfo[]>({
        queryKey: ['backups'],
        queryFn: adminService.listBackups,
    });

    const invalidate = () => queryClient.invalidateQueries({ queryKey: ['backups'] });

    const createMutation = useMutation({
        mutationFn: (name?: string) => adminService.createBackup(name),
        onSuccess: () => {
            toast.success(t('Backup created'));
            setNewName('');
            invalidate();
        },
        onError: () => toast.error(t('Backup failed')),
    });

    const renameMutation = useMutation({
        mutationFn: ({ id, name }: { id: string; name: string }) => adminService.renameBackup(id, name),
        onSuccess: () => {
            setEditingId(null);
            invalidate();
        },
        onError: () => toast.error(t('Failed to rename backup')),
    });

    const deleteMutation = useMutation({
        mutationFn: (id: string) => adminService.deleteBackup(id),
        onSuccess: () => {
            toast.success(t('Backup deleted'));
            invalidate();
        },
        onError: () => toast.error(t('Failed to delete backup')),
    });

    const startEditing = (b: BackupInfo) => {
        setEditingId(b.id);
        setEditValue(b.name);
    };

    const commitEditing = () => {
        if (editingId === null) return;
        const current = backups.find((b) => b.id === editingId);
        const trimmed = editValue.trim();
        // No-op if unchanged or blank-with-no-prior-custom-name.
        if (!current || trimmed === current.name) {
            setEditingId(null);
            return;
        }
        renameMutation.mutate({ id: editingId, name: trimmed });
    };

    const pinMutation = useMutation({
        mutationFn: ({ id, pinned }: { id: string; pinned: boolean }) =>
            adminService.setBackupPinned(id, pinned),
        onSuccess: invalidate,
        onError: () => toast.error(t('Failed to update backup')),
    });

    const restoreMutation = useMutation({
        mutationFn: adminService.restoreBackup,
        onSuccess: (result) => {
            toast.success(result.message ?? t('Restore staged. Restart the server to apply.'), { duration: 8000 });
            invalidate();
        },
        onError: (err) => toast.error(serverErrorMessage(err) ?? t('Restore failed')),
    });

    const repairMutation = useMutation({
        mutationFn: adminService.repairArtwork,
        onSuccess: (r) => {
            if (r.failedEnqueue > 0) {
                toast.warning(
                    t('Re-fetching artwork for {{count}} item(s); {{failed}} could not be queued — try again.', {
                        count: r.itemsReEnqueued,
                        failed: r.failedEnqueue,
                    }),
                    { duration: 8000 },
                );
            } else if (r.itemsReEnqueued === 0) {
                toast.success(
                    r.missingImages === 0
                        ? t('No missing artwork found.')
                        : t('No artwork could be auto-repaired (locked items or comic covers need a re-scan).'),
                );
            } else {
                toast.success(
                    t('Re-fetching artwork for {{count}} item(s). Posters will fill in as downloads complete.', {
                        count: r.itemsReEnqueued,
                    }),
                    { duration: 8000 },
                );
            }
        },
        onError: (err) => toast.error(serverErrorMessage(err) ?? t('Artwork repair failed')),
    });

    const handleDownload = async (b: BackupInfo) => {
        try {
            const { blob } = await adminService.downloadBackup(b.id);
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = toFileName(b.name);
            a.click();
            URL.revokeObjectURL(url);
        } catch {
            toast.error(t('Download failed'));
        }
    };

    const handleDelete = (b: BackupInfo) => {
        if (window.confirm(t('Delete backup "{{name}}"? This cannot be undone.', { name: b.name }))) {
            deleteMutation.mutate(b.id);
        }
    };

    const handleRestoreFile = (e: React.ChangeEvent<HTMLInputElement>) => {
        const file = e.target.files?.[0];
        // Reset the input first so re-picking the same file still fires onChange.
        e.target.value = '';
        if (!file) return;
        const confirmed = window.confirm(
            t(
                'Restore "{{file}}"?\n\nThe restore is staged now and only takes effect after the server is rebooted. ' +
                    'On the next start the current database is replaced with this backup. Continue?',
                { file: file.name },
            ),
        );
        if (confirmed) restoreMutation.mutate(file);
    };

    const buttonBase =
        'inline-flex items-center gap-2 px-4 py-2 rounded-lg text-sm font-medium transition-colors ' +
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 disabled:opacity-50';

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center gap-3 mb-4">
                <Database className="h-5 w-5 text-blue-400" />
                <h3 className="text-lg font-semibold text-white">{t('Database Backups')}</h3>
            </div>

            <p className="text-sm text-gray-400 mb-4">
                {t('Backups contain hashed credentials. Store them securely. Restores apply on the next server restart.')}
            </p>

            <div className="flex flex-wrap items-center gap-3 mb-6">
                <input
                    type="text"
                    value={newName}
                    onChange={(e) => setNewName(e.target.value)}
                    onKeyDown={(e) => {
                        if (e.key === 'Enter' && !createMutation.isPending) createMutation.mutate(newName.trim() || undefined);
                    }}
                    placeholder={t('Optional name…')}
                    maxLength={120}
                    className="px-3 py-2 rounded-lg text-sm bg-white/5 border border-white/10 text-white placeholder:text-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-400 w-48"
                />
                <button
                    type="button"
                    onClick={() => createMutation.mutate(newName.trim() || undefined)}
                    disabled={createMutation.isPending}
                    className={`${buttonBase} bg-primary hover:bg-primary/90 focus-visible:ring-blue-400 text-white`}
                >
                    {createMutation.isPending ? <RefreshCw size={16} className="animate-spin" /> : <Save size={16} />}
                    {t('Create Backup')}
                </button>

                <button
                    type="button"
                    onClick={() => fileInputRef.current?.click()}
                    disabled={restoreMutation.isPending}
                    className={`${buttonBase} bg-white/10 hover:bg-white/20 text-white`}
                >
                    {restoreMutation.isPending ? <RefreshCw size={16} className="animate-spin" /> : <Upload size={16} />}
                    {t('Restore from File')}
                </button>
                <input
                    ref={fileInputRef}
                    type="file"
                    accept=".zip"
                    onChange={handleRestoreFile}
                    className="hidden"
                />
            </div>

            <div className="mb-6 rounded-lg border border-white/10 bg-white/[0.03] p-4">
                <div className="flex items-start justify-between gap-4 flex-wrap">
                    <p className="text-sm text-gray-400 max-w-xl">
                        {t('Artwork is cached on disk and is not included in backups. Restoring a backup automatically starts an artwork scan on the next server start to re-fetch missing posters from your metadata providers — so you normally don’t need this button. Use it to re-run that scan on demand.')}
                    </p>
                    <button
                        type="button"
                        onClick={() => repairMutation.mutate()}
                        disabled={repairMutation.isPending}
                        className={`${buttonBase} bg-white/10 hover:bg-white/20 text-white`}
                        title={t('Re-download artwork whose cached files are missing')}
                    >
                        {repairMutation.isPending ? <RefreshCw size={16} className="animate-spin" /> : <Image size={16} />}
                        {t('Repair Artwork')}
                    </button>
                </div>
                <p className="text-xs text-gray-500 mt-2">
                    {t('Locked items are skipped; comic covers are recovered by a library re-scan.')}
                </p>
            </div>

            {isLoading ? (
                <div className="text-center py-6">
                    <RefreshCw className="animate-spin w-6 h-6 text-primary mx-auto" />
                </div>
            ) : backups.length === 0 ? (
                <p className="text-sm text-gray-500 py-4">{t('No backups yet.')}</p>
            ) : (
                <div className="overflow-x-auto">
                    <table className="w-full text-sm">
                        <thead>
                            <tr className="text-left text-gray-400 border-b border-white/10">
                                <th className="pb-2 font-medium">{t('Name')}</th>
                                <th className="pb-2 font-medium">{t('Created')}</th>
                                <th className="pb-2 font-medium">{t('Size')}</th>
                                <th className="pb-2 font-medium text-right">{t('Actions')}</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-white/5">
                            {backups.map((b) => (
                                <tr key={b.id} className="text-gray-300">
                                    <td className="py-2 pr-3 max-w-xs">
                                        {editingId === b.id ? (
                                            <span className="inline-flex items-center gap-1">
                                                <input
                                                    autoFocus
                                                    type="text"
                                                    value={editValue}
                                                    maxLength={120}
                                                    onChange={(e) => setEditValue(e.target.value)}
                                                    onKeyDown={(e) => {
                                                        if (e.key === 'Enter') commitEditing();
                                                        else if (e.key === 'Escape') setEditingId(null);
                                                    }}
                                                    onBlur={commitEditing}
                                                    className="px-2 py-1 rounded text-sm bg-white/10 border border-white/20 text-white focus:outline-none focus:ring-2 focus:ring-blue-400 w-44"
                                                />
                                                <button type="button" onMouseDown={(e) => { e.preventDefault(); commitEditing(); }} title={t('Save')}
                                                    className="p-1 rounded text-green-400 hover:bg-white/10"><Check size={14} /></button>
                                                <button type="button" onMouseDown={(e) => { e.preventDefault(); setEditingId(null); }} title={t('Cancel')}
                                                    className="p-1 rounded text-gray-400 hover:bg-white/10"><X size={14} /></button>
                                            </span>
                                        ) : (
                                            <button
                                                type="button"
                                                onClick={() => startEditing(b)}
                                                title={t('Click to rename')}
                                                className="group inline-flex items-center gap-1.5 text-left text-gray-200 hover:text-white"
                                            >
                                                <span className="truncate">{b.name}</span>
                                                <Pencil size={12} className="opacity-0 group-hover:opacity-60 shrink-0" />
                                            </button>
                                        )}
                                    </td>
                                    <td className="py-2">{new Date(b.createdAtUtc).toLocaleString()}</td>
                                    <td className="py-2">{formatBytes(b.sizeBytes)}</td>
                                    <td className="py-2">
                                        <div className="flex items-center justify-end gap-1">
                                            <button
                                                type="button"
                                                onClick={() => pinMutation.mutate({ id: b.id, pinned: !b.isPinned })}
                                                className="p-1.5 rounded hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-gray-400 hover:text-white"
                                                title={b.isPinned ? t('Unpin') : t('Pin (protect from rotation)')}
                                            >
                                                {b.isPinned ? <Pin size={16} className="text-blue-400" /> : <PinOff size={16} />}
                                            </button>
                                            <button
                                                type="button"
                                                onClick={() => handleDownload(b)}
                                                className="p-1.5 rounded hover:bg-primary/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-primary"
                                                title={t('Download')}
                                            >
                                                <Download size={16} />
                                            </button>
                                            <button
                                                type="button"
                                                onClick={() => handleDelete(b)}
                                                disabled={deleteMutation.isPending}
                                                className="p-1.5 rounded hover:bg-red-500/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-400 text-gray-400 hover:text-red-400 disabled:opacity-50"
                                                title={t('Delete')}
                                            >
                                                <Trash2 size={16} />
                                            </button>
                                        </div>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}
        </div>
    );
}
