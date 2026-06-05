import { useState } from 'react';
import { Clock, Play, RefreshCw, CheckCircle, XCircle } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { adminService, type ScheduledTaskStatus } from '../../services/adminService';

function relativeTime(iso: string | null): string {
    if (!iso) return '—';
    const then = new Date(iso).getTime();
    const diffSec = Math.round((Date.now() - then) / 1000);
    if (diffSec < 60) return `${diffSec}s ago`;
    if (diffSec < 3600) return `${Math.round(diffSec / 60)}m ago`;
    if (diffSec < 86400) return `${Math.round(diffSec / 3600)}h ago`;
    return new Date(iso).toLocaleDateString();
}

function ResultBadge({ result }: { result: string | null }) {
    if (!result) return <span className="text-gray-500 text-xs">never run</span>;
    if (result === 'Success')
        return <span className="inline-flex items-center gap-1 text-green-400 text-xs"><CheckCircle size={14} /> Success</span>;
    if (result === 'Failed')
        return <span className="inline-flex items-center gap-1 text-red-400 text-xs"><XCircle size={14} /> Failed</span>;
    return <span className="text-gray-400 text-xs">{result}</span>;
}

export function ScheduledTasksCard() {
    const { t } = useTranslation();
    const queryClient = useQueryClient();

    // Triggered tasks run asynchronously, so after "Run now" we poll briefly to catch the
    // result, then stop. Normally the card just fetches once on open (and on tab focus) —
    // the tasks run on daily/hourly schedules, so there's nothing to watch the rest of the time.
    const [pollUntil, setPollUntil] = useState(0);

    const { data: tasks = [], isLoading } = useQuery<ScheduledTaskStatus[]>({
        queryKey: ['scheduledTasks'],
        queryFn: adminService.listTasks,
        refetchInterval: () => (Date.now() < pollUntil ? 5000 : false),
    });

    const triggerMutation = useMutation({
        mutationFn: adminService.triggerTask,
        onSuccess: () => {
            toast.success(t('Task triggered'));
            setPollUntil(Date.now() + 60_000); // poll ~5s for up to a minute to catch completion
            queryClient.invalidateQueries({ queryKey: ['scheduledTasks'] });
        },
        onError: () => toast.error(t('Failed to trigger task')),
    });

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center gap-3 mb-4">
                <Clock className="h-5 w-5 text-blue-400" />
                <h3 className="text-lg font-semibold text-white">{t('Background Tasks')}</h3>
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
                                <th className="pb-2 font-medium">{t('Task')}</th>
                                <th className="pb-2 font-medium">{t('Last Run')}</th>
                                <th className="pb-2 font-medium">{t('Result')}</th>
                                <th className="pb-2 font-medium">{t('Next Run')}</th>
                                <th className="pb-2 font-medium text-right">{t('Actions')}</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-white/5">
                            {tasks.map((task) => (
                                <tr key={task.name} className="text-gray-300">
                                    <td className="py-2">
                                        <div className="font-medium text-white">{task.name}</div>
                                        <div className="text-xs text-gray-500">{task.description}</div>
                                        {task.lastError && (
                                            <div className="text-xs text-red-400/80 mt-0.5" title={task.lastError}>
                                                {task.lastError}
                                            </div>
                                        )}
                                    </td>
                                    <td className="py-2">{relativeTime(task.lastRunUtc)}</td>
                                    <td className="py-2"><ResultBadge result={task.lastResult} /></td>
                                    <td className="py-2">
                                        {task.schedule === 'EventDriven'
                                            ? <span className="text-xs text-gray-500">{t('event-driven')}</span>
                                            : relativeTimeFuture(task.nextRunUtc)}
                                    </td>
                                    <td className="py-2 text-right">
                                        {task.supportsManualTrigger && (
                                            <button
                                                type="button"
                                                onClick={() => triggerMutation.mutate(task.name)}
                                                disabled={triggerMutation.isPending}
                                                className="inline-flex items-center gap-1 px-2.5 py-1.5 text-xs rounded hover:bg-primary/20 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-primary disabled:opacity-50"
                                                title={t('Run now')}
                                            >
                                                <Play size={14} /> {t('Run now')}
                                            </button>
                                        )}
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

function relativeTimeFuture(iso: string | null): string {
    if (!iso) return '—';
    const diffSec = Math.round((new Date(iso).getTime() - Date.now()) / 1000);
    if (diffSec <= 0) return 'imminent';
    if (diffSec < 3600) return `in ${Math.round(diffSec / 60)}m`;
    if (diffSec < 86400) return `in ${Math.round(diffSec / 3600)}h`;
    return new Date(iso).toLocaleString();
}
