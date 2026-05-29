import { useRef } from 'react';
import { Database, Download, Save, Upload, Pin, PinOff, RefreshCw } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { adminService, type BackupInfo } from '../../services/adminService';

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

    const { data: backups = [], isLoading } = useQuery<BackupInfo[]>({
        queryKey: ['backups'],
        queryFn: adminService.listBackups,
    });

    const invalidate = () => queryClient.invalidateQueries({ queryKey: ['backups'] });

    const createMutation = useMutation({
        mutationFn: adminService.createBackup,
        onSuccess: () => {
            toast.success(t('Backup created'));
            invalidate();
        },
        onError: () => toast.error(t('Backup failed')),
    });

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
        onError: () => toast.error(t('Restore failed')),
    });

    const handleDownload = async (id: string) => {
        try {
            const { blob, filename } = await adminService.downloadBackup(id);
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = filename;
            a.click();
            URL.revokeObjectURL(url);
        } catch {
            toast.error(t('Download failed'));
        }
    };

    const handleRestoreFile = (e: React.ChangeEvent<HTMLInputElement>) => {
        const file = e.target.files?.[0];
        if (file) restoreMutation.mutate(file);
        e.target.value = '';
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

            <div className="flex flex-wrap gap-3 mb-6">
                <button
                    type="button"
                    onClick={() => createMutation.mutate()}
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
                                <th className="pb-2 font-medium">{t('Backup')}</th>
                                <th className="pb-2 font-medium">{t('Created')}</th>
                                <th className="pb-2 font-medium">{t('Size')}</th>
                                <th className="pb-2 font-medium text-right">{t('Actions')}</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-white/5">
                            {backups.map((b) => (
                                <tr key={b.id} className="text-gray-300">
                                    <td className="py-2 font-mono text-xs">{b.id}</td>
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
                                                onClick={() => handleDownload(b.id)}
                                                className="p-1.5 rounded hover:bg-primary/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-primary"
                                                title={t('Download')}
                                            >
                                                <Download size={16} />
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
