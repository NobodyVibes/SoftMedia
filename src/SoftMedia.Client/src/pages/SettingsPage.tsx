import { useState, useEffect } from 'react';
import { useParams } from 'react-router-dom';
import { Settings, Users, Library as LibraryIcon, Save, RefreshCw, Database, Play, Plus, AlertTriangle, RotateCcw, X } from 'lucide-react';
import { cn } from '../lib/utils';
import { Combobox } from '../components/ui/Combobox';
import { settingsService, type AppSetting } from '../services/settingsService';
import { libraryService } from '../services/libraryService';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '../store/authStore';
import ClientSettings from './settings/ClientSettings';
import { UserListTable } from '../components/admin/UserListTable';
import { InviteManager } from '../components/admin/InviteManager';
import { LibraryListTable } from '../components/library/LibraryListTable';
import { LibraryForm } from '../components/library/LibraryForm';
import { ConfirmationModal } from '../components/modals/ConfirmationModal';
import { LibraryScanProgress } from '../components/library/LibraryScanProgress';
import type { Library, LibraryScanJob, FileWatcherIssue } from '../types';
import { adminService } from '../services/adminService';
import { notificationService, type OMDbUsage, type SystemNotification } from '../services/notificationService';

// Admin Dashboard Component
function AdminDashboard() {
    const queryClient = useQueryClient();
    const { t } = useTranslation();

    const { data: issues = [], isLoading } = useQuery<FileWatcherIssue[]>({
        queryKey: ['fileWatcherIssues'],
        queryFn: adminService.getFileWatcherIssues,
        refetchInterval: 10000, // Poll every 10s
    });

    // OMDb usage query
    const { data: omdbUsage } = useQuery<OMDbUsage>({
        queryKey: ['omdbUsage'],
        queryFn: notificationService.getOMDbUsage,
        refetchInterval: 30000, // Poll every 30s
    });

    // System notifications query (available for future dashboard features)
    const { data: _systemNotifications = [] } = useQuery<SystemNotification[]>({
        queryKey: ['systemNotifications'],
        queryFn: notificationService.getNotifications,
        refetchInterval: 30000,
    });

    const retryMutation = useMutation({
        mutationFn: adminService.retryFile,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['fileWatcherIssues'] });
            toast.success(t('File retry scheduled'));
        },
        onError: () => toast.error(t('Failed to retry file')),
    });

    const dismissMutation = useMutation({
        mutationFn: adminService.clearIssue,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['fileWatcherIssues'] });
            toast.success(t('Issue dismissed'));
        },
        onError: () => toast.error(t('Failed to dismiss issue')),
    });

    const formatDate = (dateStr: string) => {
        return new Date(dateStr).toLocaleString();
    };

    const getUsageColor = (used: number, limit: number) => {
        const pct = (used / limit) * 100;
        if (pct >= 100) return 'text-red-400 bg-red-500/20';
        if (pct >= 90) return 'text-amber-400 bg-amber-500/20';
        if (pct >= 75) return 'text-yellow-400 bg-yellow-500/20';
        return 'text-green-400 bg-green-500/20';
    };

    return (
        <div className="space-y-8">
            {/* API Usage Warnings */}
            {omdbUsage && omdbUsage.used > 0 && (
                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <div className="flex items-center gap-3 mb-4">
                        <Database className="h-5 w-5 text-blue-400" />
                        <h3 className="text-lg font-semibold text-white">{t('API Usage')}</h3>
                    </div>

                    <div className="flex items-center gap-4">
                        <div className={`px-3 py-2 rounded-lg ${getUsageColor(omdbUsage.used, omdbUsage.limit)}`}>
                            <span className="text-xs opacity-70">OMDb</span>
                            <p className="font-semibold">
                                {omdbUsage.used.toLocaleString()} / {omdbUsage.limit.toLocaleString()}
                            </p>
                        </div>
                        {omdbUsage.isExhausted && (
                            <div className="flex-1 p-3 bg-red-500/10 border border-red-500/30 rounded-lg">
                                <p className="text-sm text-red-300 font-medium">
                                    ⚠️ {t('Daily limit reached')}
                                </p>
                                <p className="text-xs text-red-400/80 mt-0.5">
                                    {t('Movie metadata will be skipped until midnight UTC.')}
                                </p>
                            </div>
                        )}
                    </div>
                </div>
            )}

            {/* File Watcher Issues */}
            <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                <div className="flex items-center gap-3 mb-6">
                    <AlertTriangle className="h-5 w-5 text-amber-400" />
                    <h3 className="text-lg font-semibold text-white">{t('File Watcher Issues')}</h3>
                    <span className="px-2 py-0.5 bg-amber-500/20 text-amber-300 rounded-full text-xs font-medium">
                        {issues.length}
                    </span>
                </div>

                {isLoading ? (
                    <div className="text-center py-8">
                        <RefreshCw className="animate-spin w-6 h-6 text-primary mx-auto" />
                    </div>
                ) : issues.length === 0 ? (
                    <div className="text-center py-8 text-gray-400">
                        <AlertTriangle className="w-12 h-12 mx-auto mb-3 opacity-30" />
                        <p>{t('No file watcher issues')}</p>
                        <p className="text-sm text-gray-500 mt-1">{t('All files are processing normally')}</p>
                    </div>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="w-full">
                            <thead>
                                <tr className="text-left text-gray-400 text-sm border-b border-white/10">
                                    <th className="pb-3 font-medium">{t('File')}</th>
                                    <th className="pb-3 font-medium">{t('Status')}</th>
                                    <th className="pb-3 font-medium">{t('First Seen')}</th>
                                    <th className="pb-3 font-medium">{t('Last Checked')}</th>
                                    <th className="pb-3 font-medium text-right">{t('Actions')}</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-white/5">
                                {issues.map((issue) => (
                                    <tr key={issue.path} className="text-gray-300">
                                        <td className="py-3">
                                            <div className="font-medium truncate max-w-xs" title={issue.path}>
                                                {issue.fileName}
                                            </div>
                                            <div className="text-xs text-gray-500 truncate max-w-xs" title={issue.path}>
                                                {issue.path}
                                            </div>
                                        </td>
                                        <td className="py-3">
                                            <span className="px-2 py-1 bg-red-500/20 text-red-300 rounded text-xs">
                                                {issue.status}
                                            </span>
                                        </td>
                                        <td className="py-3 text-sm">{formatDate(issue.firstSeen)}</td>
                                        <td className="py-3 text-sm">{formatDate(issue.lastChecked)}</td>
                                        <td className="py-3 text-right">
                                            <div className="flex items-center justify-end gap-2">
                                                {issue.canRetry && (
                                                    <button
                                                        onClick={() => retryMutation.mutate(issue.path)}
                                                        disabled={retryMutation.isPending}
                                                        className="p-1.5 hover:bg-primary/20 rounded transition-colors text-primary"
                                                        title={t('Retry')}
                                                    >
                                                        <RotateCcw size={16} />
                                                    </button>
                                                )}
                                                <button
                                                    onClick={() => dismissMutation.mutate(issue.path)}
                                                    disabled={dismissMutation.isPending}
                                                    className="p-1.5 hover:bg-red-500/20 rounded transition-colors text-gray-400 hover:text-red-400"
                                                    title={t('Dismiss')}
                                                >
                                                    <X size={16} />
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
        </div>
    );
}

export default function SettingsPage() {
    const { section, subsection } = useParams<{ section?: string; subsection?: string }>();

    // Build activeTab from URL params
    const activeTab = subsection
        ? `${section}-${subsection}`
        : section || 'server';

    const queryClient = useQueryClient();
    const [localSettings, setLocalSettings] = useState<AppSetting[]>([]);
    const { t, i18n } = useTranslation();

    // Library State
    const [isLibraryFormOpen, setIsLibraryFormOpen] = useState(false);
    const [editingLibrary, setEditingLibrary] = useState<Library | undefined>(undefined);
    const [libraryToDelete, setLibraryToDelete] = useState<Library | null>(null);

    const isAdmin = useAuthStore(state => state.user?.role === 'Admin');

    // Fetch Settings - Only for Admin or when not on Client section
    const { data: settings, isLoading } = useQuery({
        queryKey: ['settings'],
        queryFn: settingsService.getAll,
        enabled: isAdmin && section !== 'client',
    });

    // Fetch Libraries - Only for Admin
    const { data: libraries, isLoading: isLoadingLibraries } = useQuery({
        queryKey: ['libraries'],
        queryFn: libraryService.getAll,
        enabled: isAdmin,
    });

    // Fetch Scan Queue with polling when scans are active
    const { data: scanQueue = [] } = useQuery<LibraryScanJob[]>({
        queryKey: ['scanQueue'],
        queryFn: libraryService.getScanQueue,
        refetchInterval: (query) => {
            const jobs = query.state.data ?? [];
            const hasActiveScans = jobs.some((j: LibraryScanJob) => j.status === 'Running' || j.status === 'Queued');
            return hasActiveScans ? 2000 : false; // Poll every 2s when active, stop when idle
        },
    });

    // Fetch OMDb usage when OMDb is selected as movie provider
    const movieProviderSetting = localSettings.find(s => s.key === 'MovieProvider');
    const isOMDbSelected = movieProviderSetting?.value === 'OMDb';

    const { data: omdbUsage } = useQuery({
        queryKey: ['omdb-usage'],
        queryFn: notificationService.getOMDbUsage,
        enabled: isOMDbSelected,
        refetchInterval: 30000, // Refresh every 30 seconds
    });

    // Update local state when data is fetched
    useEffect(() => {
        if (settings) {
            setLocalSettings(settings);
        }
    }, [settings]);

    // Update Mutation
    const updateMutation = useMutation({
        mutationFn: settingsService.update,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['settings'] });
            toast.success(t('Settings saved successfully'));

            // Update language if it changed
            const langSetting = localSettings.find(s => s.key === 'Language');
            if (langSetting && langSetting.value !== i18n.language) {
                i18n.changeLanguage(langSetting.value);
            }
        },
        onError: () => {
            toast.error(t('Failed to save settings'));
        }
    });

    // Library Mutations
    const createLibraryMutation = useMutation({
        mutationFn: libraryService.create,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['libraries'] });
            toast.success(t('Library created successfully'));
            setIsLibraryFormOpen(false);
        },
        onError: (error: unknown) => {
            const errorMessage = (error as { response?: { data?: string } })?.response?.data
                || (error as Error)?.message
                || 'Failed to create library';
            toast.error(errorMessage);
        }
    });

    const updateLibraryMutation = useMutation({
        mutationFn: ({ id, data }: { id: string; data: { name: string; type: string; paths: string[] } }) =>
            libraryService.update(id, data),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['libraries'] });
            toast.success(t('Library updated successfully'));
            setIsLibraryFormOpen(false);
            setEditingLibrary(undefined);
        },
        onError: () => {
            toast.error(t('Failed to update library'));
        }
    });

    const deleteLibraryMutation = useMutation({
        mutationFn: libraryService.delete,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['libraries'] });
            toast.success(t('Library deleted successfully'));
        },
        onError: () => {
            toast.error(t('Failed to delete library'));
        }
    });

    const reorderLibraryMutation = useMutation({
        mutationFn: libraryService.reorderLibraries,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['libraries'] });
            toast.success('Library order updated');
        },
        onError: () => toast.error('Failed to reorder libraries'),
    });

    const scanLibraryMutation = useMutation({
        mutationFn: libraryService.scanLibrary,
        onSuccess: (job: LibraryScanJob) => {
            queryClient.invalidateQueries({ queryKey: ['scanQueue'] });
            toast.success(`Scan queued for ${job.libraryName}`);
        },
        onError: () => toast.error('Failed to start library scan'),
    });

    const refreshHeroCacheMutation = useMutation({
        mutationFn: adminService.refreshHeroCache,
        onSuccess: () => {
            toast.success(t('Hero cache refresh completed'));
        },
        onError: () => {
            toast.error(t('Failed to refresh hero cache'));
        }
    });

    const handleSave = () => {
        updateMutation.mutate(localSettings);
    };

    const handleChange = (key: string, value: string) => {
        setLocalSettings(prev => prev.map(s => s.key === key ? { ...s, value } : s));
    };

    const handleLibrarySubmit = async (data: { name: string; type: string; paths: string[] }) => {
        if (editingLibrary) {
            await updateLibraryMutation.mutateAsync({ id: editingLibrary.id, data });
        } else {
            await createLibraryMutation.mutateAsync(data);
        }
    };

    const languageOptions = [
        "en-US", "es-ES", "fr-FR", "de-DE", "it-IT", "pt-BR", "ja-JP", "zh-CN", "ru-RU"
    ];

    const logLevelOptions = [
        "Trace", "Debug", "Info", "Warning", "Error", "Critical"
    ];

    const movieProviders = ["Wikidata", "OMDb"];
    const tvProviders = ["TVMaze"];
    const musicProviders = ["MusicBrainz", "Embedded"];
    const bookProviders = ["Open Library"];
    const comicProviders = ["ComicInfo", "Wikidata"];
    const comicFallbackProviders = ["Wikidata", "ComicInfo", "None"];
    const gameProviders = ["Wikidata"];
    const photoProviders = ["Exif"];

    // Transcoding options
    const hardwareAccelOptions = [
        { value: "none", label: "None (CPU Only)" },
        { value: "nvidia", label: "NVIDIA (NVENC)" },
        { value: "amd", label: "AMD (AMF)" },
        { value: "intel", label: "Intel (QuickSync)" },
    ];

    const presetOptions = [
        { value: "ultrafast", label: "Ultrafast (Lowest CPU)" },
        { value: "superfast", label: "Superfast" },
        { value: "veryfast", label: "Veryfast (Default)" },
        { value: "faster", label: "Faster" },
        { value: "fast", label: "Fast" },
        { value: "medium", label: "Medium" },
        { value: "slow", label: "Slow" },
        { value: "slower", label: "Slower" },
        { value: "veryslow", label: "Veryslow (Best Quality)" },
    ];

    const resolutionOptions = [
        { value: "original", label: "Original (No Scaling) - Varies" },
        { value: "4k", label: "4K (3840p) - 25-40 Mbps" },
        { value: "1080p", label: "1080p Full HD - 8-12 Mbps" },
        { value: "720p", label: "720p HD - 3-5 Mbps" },
    ];

    const toneMappingOptions = [
        { value: "hable", label: "Hable (Filmic)" },
        { value: "reinhard", label: "Reinhard (Photographic)" },
        { value: "mobius", label: "Mobius (Smooth)" },
    ];

    const outputCodecOptions = [
        { value: "auto", label: "Auto (Server picks most efficient client-supported codec)" },
        { value: "h264", label: "H.264 (Most compatible)" },
        { value: "hevc", label: "HEVC/H.265 (More efficient)" },
    ];

    // CRF quality label helper
    const getCRFLabel = (crf: number): string => {
        if (crf <= 0) return "Lossless";
        if (crf <= 18) return "Excellent";
        if (crf <= 23) return "Good";
        if (crf <= 28) return "Fair";
        if (crf <= 35) return "Poor";
        return "Worst";
    };

    // Calculate CRF slider thumb position percentage
    const getCRFPosition = (crf: number): number => {
        return (crf / 51) * 100;
    };

    // Format setting key to human-readable label, preserving acronyms like HDR, CRF, etc.
    const formatSettingLabel = (key: string): string => {
        // Split on transitions from lowercase to uppercase, or before an uppercase followed by lowercase
        // This handles: "PreserveHDR" -> "Preserve HDR", "TranscodeCRF" -> "Transcode CRF"
        return key
            .replace(/([a-z])([A-Z])/g, '$1 $2')  // lowercase -> uppercase: add space
            .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')  // consecutive uppercase before lowercase: add space
            .trim();
    };

    // Helper to render settings by group
    const renderSettingsGroup = (groupName: string) => {
        let groupSettings = localSettings.filter(s => s.group === groupName && s.key !== 'DisableTranscoding');

        // Explicit ordering for Transcoding group
        if (groupName === 'Transcoding') {
            const transcodingOrder = [
                'EnableTranscoding',
                'EnableAV1Encoding',
                'ForceDirectPlayWhenPossible',
                'TranscodePreset',
                'OutputVideoCodec',
                'HardwareAcceleration',
                'MaxSimultaneousTranscodes',
                'TranscodeThreadCount'
            ];
            groupSettings = groupSettings.sort((a, b) => {
                const aIndex = transcodingOrder.indexOf(a.key);
                const bIndex = transcodingOrder.indexOf(b.key);
                return (aIndex === -1 ? 999 : aIndex) - (bIndex === -1 ? 999 : bIndex);
            });
        }

        // Explicit ordering for Streaming group
        if (groupName === 'Streaming') {
            const streamingOrder = [
                'DefaultStreamingQuality',
                'MaxTranscodeResolution',
                'TranscodeCRF',
                'PreserveHDR',
                'ToneMappingAlgorithm',
                'MaxStreamingBitrate',
                'DefaultAudioChannels'
            ];
            groupSettings = groupSettings.sort((a, b) => {
                const aIndex = streamingOrder.indexOf(a.key);
                const bIndex = streamingOrder.indexOf(b.key);
                return (aIndex === -1 ? 999 : aIndex) - (bIndex === -1 ? 999 : bIndex);
            });
        }

        // Dedicated layout for Scanning group
        if (groupName === 'Scanning') {
            const fileWatcher = groupSettings.find(s => s.key === 'EnableFileWatcher');
            const interval = groupSettings.find(s => s.key === 'MetadataRefreshIntervalDays');
            const mode = groupSettings.find(s => s.key === 'MetadataRefreshMode');
            const startup = groupSettings.find(s => s.key === 'MetadataRefreshOnStartup');

            return (
                <div className="space-y-6">
                    {/* File Watcher */}
                    {fileWatcher && (
                        <div key={fileWatcher.key} className="flex flex-col gap-2">
                            <div className="flex items-center gap-3">
                                <button
                                    onClick={() => handleChange(fileWatcher.key, fileWatcher.value === 'true' ? 'false' : 'true')}
                                    className={cn(
                                        "w-12 h-6 rounded-full transition-colors relative flex-shrink-0",
                                        fileWatcher.value === 'true' ? "bg-primary" : "bg-white/10"
                                    )}
                                >
                                    <div className={cn(
                                        "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                        fileWatcher.value === 'true' ? "left-7" : "left-1"
                                    )} />
                                </button>
                                <label className="text-sm font-medium text-gray-300">{t(formatSettingLabel(fileWatcher.key))}</label>
                            </div>
                            {fileWatcher.description && <p className="text-xs text-gray-500">{t(fileWatcher.description)}</p>}

                            {/* Manual Hero Cache Refresh Button */}
                            <div className="mt-4 pt-4 border-t border-white/5">
                                <button
                                    onClick={() => refreshHeroCacheMutation.mutate()}
                                    disabled={refreshHeroCacheMutation.isPending}
                                    className="flex items-center gap-2 px-4 py-2 bg-violet-500/20 hover:bg-violet-500/30 text-violet-300 rounded-lg transition-all text-sm font-semibold border border-violet-500/30 disabled:opacity-50"
                                >
                                    <RefreshCw className={cn("w-4 h-4", refreshHeroCacheMutation.isPending && "animate-spin")} />
                                    {refreshHeroCacheMutation.isPending ? t('Refreshing Hero Cache...') : t('Update Hero Cache')}
                                </button>
                                <p className="text-[10px] text-gray-500 mt-2">
                                    {t('Forces the hero section on the home page to update immediately.')}
                                </p>
                            </div>
                        </div>
                    )}

                    {/* Metadata Strategy Group */}
                    {(interval || mode || startup) && (
                        <div className="bg-white/5 p-5 rounded-xl border border-white/10 space-y-5">
                            <div className="flex items-center justify-between border-b border-white/5 pb-3">
                                <div className="flex items-center gap-2">
                                    <RefreshCw className="w-4 h-4 text-primary" />
                                    <h3 className="text-sm font-semibold text-white/90">Metadata Refresh Strategy</h3>
                                </div>
                                <button
                                    onClick={() => {
                                        toast.promise(settingsService.triggerMetadataRefresh(), {
                                            loading: 'Triggering refresh...',
                                            success: 'Background refresh started',
                                            error: 'Failed to start refresh'
                                        });
                                    }}
                                    className="text-xs px-3 py-1.5 bg-primary/20 hover:bg-primary/30 text-primary rounded transition-colors font-medium flex items-center gap-2"
                                >
                                    <Play size={12} fill="currentColor" />
                                    Run Now
                                </button>
                            </div>

                            <div className="grid gap-6">
                                {interval && (
                                    <div className="flex flex-col gap-2">
                                        <label className="text-sm font-medium text-gray-300">Refresh Interval</label>
                                        <div className="flex items-center gap-3">
                                            <input
                                                type="number"
                                                min="0"
                                                max="365"
                                                value={interval.value}
                                                onChange={(e) => handleChange(interval.key, e.target.value)}
                                                className="w-24 bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none transition-colors"
                                            />
                                            <span className="text-sm text-gray-400">
                                                {parseInt(interval.value) === 0 ? '(Disabled)' : 'days'}
                                            </span>
                                        </div>
                                        {interval.description && <p className="text-xs text-gray-500">{t(interval.description)}</p>}
                                    </div>
                                )}

                                {mode && (
                                    <div className="flex flex-col gap-2">
                                        <label className="text-sm font-medium text-gray-300">Refresh Mode</label>
                                        <Combobox
                                            value={mode.value}
                                            onChange={(val) => handleChange(mode.key, val)}
                                            options={['Running', 'Variable', 'All']}
                                            placeholder="Select refresh mode..."
                                            className="max-w-md"
                                        />
                                        {mode.description && <p className="text-xs text-gray-500">{t(mode.description)}</p>}
                                    </div>
                                )}

                                {startup && (
                                    <div className="flex flex-col gap-2">
                                        <label className="text-sm font-medium text-gray-300">Run on Startup</label>
                                        <div className="flex items-center gap-3">
                                            <button
                                                onClick={() => handleChange(startup.key, startup.value === 'true' ? 'false' : 'true')}
                                                className={cn(
                                                    "w-12 h-6 rounded-full transition-colors relative flex-shrink-0",
                                                    startup.value === 'true' ? "bg-primary" : "bg-white/10"
                                                )}
                                            >
                                                <div className={cn(
                                                    "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                                    startup.value === 'true' ? "left-7" : "left-1"
                                                )} />
                                            </button>
                                            <span className="text-sm text-gray-400">{startup.value === 'true' ? 'Enabled' : 'Disabled'}</span>
                                        </div>
                                        {startup.description && <p className="text-xs text-gray-500">{t(startup.description)}</p>}
                                    </div>
                                )}
                            </div>
                        </div>
                    )}
                </div>
            );
        }

        if (groupSettings.length === 0) return <p className="text-gray-500 italic">No settings available.</p>;

        return (
            <div className="space-y-6">
                {groupSettings.map(setting => {
                    const isTranscodingOrStreaming = groupName === 'Transcoding' || groupName === 'Streaming';
                    const isToggle = (setting.value === 'true' || setting.value === 'false') && !['HardwareAcceleration', 'PreserveHDR'].includes(setting.key);

                    return (
                        <div key={setting.key} className="flex flex-col gap-2">
                            {/* For Transcoding/Streaming toggles, show toggle + label inline */}
                            {isTranscodingOrStreaming && isToggle ? (
                                <div className="flex items-center gap-3">
                                    <button
                                        onClick={() => handleChange(setting.key, setting.value === 'true' ? 'false' : 'true')}
                                        className={cn(
                                            "w-12 h-6 rounded-full transition-colors relative flex-shrink-0",
                                            setting.value === 'true' ? "bg-primary" : "bg-white/10"
                                        )}
                                    >
                                        <div className={cn(
                                            "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                            setting.value === 'true' ? "left-7" : "left-1"
                                        )} />
                                    </button>
                                    <label className="text-sm font-medium text-gray-300">{t(formatSettingLabel(setting.key))}</label>
                                </div>
                            ) : (
                                /* For other settings, show label first (if applicable) */
                                !['MusicProviderPrimary', 'MusicProviderFallback'].includes(setting.key) && (
                                    <label className="text-sm font-medium text-gray-300">{t(formatSettingLabel(setting.key))}</label>
                                )
                            )}

                            {setting.key === 'AllowUserSignup' ? (
                                <Combobox
                                    value={setting.value === 'true' ? 'Enabled' : setting.value === 'false' ? 'Disabled' : setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={["Disabled", "InviteOnly", "Enabled"]}
                                    placeholder="Select signup mode..."
                                    className="max-w-md"
                                />
                            ) : isTranscodingOrStreaming && isToggle ? (
                                /* Toggle already rendered above for Transcoding/Streaming */
                                null
                            ) : (setting.value === 'true' || setting.value === 'false') && !['HardwareAcceleration', 'PreserveHDR'].includes(setting.key) ? (
                                <div className="flex items-center gap-3">
                                    <button
                                        onClick={() => handleChange(setting.key, setting.value === 'true' ? 'false' : 'true')}
                                        className={cn(
                                            "w-12 h-6 rounded-full transition-colors relative",
                                            setting.value === 'true' ? "bg-primary" : "bg-white/10"
                                        )}
                                    >
                                        <div className={cn(
                                            "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                            setting.value === 'true' ? "left-7" : "left-1"
                                        )} />
                                    </button>
                                    <span className="text-sm text-gray-400">{setting.value === 'true' ? 'Enabled' : 'Disabled'}</span>
                                </div>
                            ) : setting.key === 'Language' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={languageOptions}
                                    placeholder="Select language..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'LogLevel' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={logLevelOptions}
                                    placeholder="Select log level..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'MovieProvider' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={movieProviders}
                                    placeholder="Select movie provider..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'TVProvider' ? (
                                <>
                                    <Combobox
                                        value={setting.value}
                                        onChange={(val) => handleChange(setting.key, val)}
                                        options={tvProviders}
                                        placeholder="Select TV provider..."
                                        className="max-w-md"
                                    />
                                    {setting.value === 'TVMaze' && (
                                        <div className="mt-2 p-3 bg-blue-500/10 border border-blue-500/20 rounded-md max-w-md">
                                            <p className="text-xs text-blue-200">
                                                Data provided by TVMaze for free. Please consider supporting them: <a href="https://www.tvmaze.com/premium" target="_blank" rel="noopener noreferrer" className="underline hover:text-white font-bold">Donate to TVMaze</a>
                                            </p>
                                        </div>
                                    )}
                                </>
                            ) : setting.key === 'MusicProviderPrimary' ? (
                                <>
                                    <label className="text-sm font-medium text-gray-300">Music Providers</label>
                                    <div className="bg-white/5 p-4 rounded-lg border border-white/10 space-y-4">

                                        <div className="space-y-2">
                                            <label className="text-xs text-gray-400 block">Primary Provider (First Choice)</label>
                                            <Combobox
                                                value={setting.value}
                                                onChange={(val) => handleChange(setting.key, val)}
                                                options={musicProviders}
                                                placeholder="Select primary music provider..."
                                                className="w-full"
                                            />
                                        </div>
                                        {localSettings.find(s => s.key === 'MusicProviderFallback') && (
                                            <div className="space-y-2">
                                                <label className="text-xs text-gray-400 block">Fallback Provider (If Primary fails)</label>
                                                <Combobox
                                                    value={localSettings.find(s => s.key === 'MusicProviderFallback')!.value}
                                                    onChange={(val) => handleChange('MusicProviderFallback', val)}
                                                    options={musicProviders}
                                                    placeholder="Select fallback music provider..."
                                                    className="w-full"
                                                />
                                            </div>
                                        )}
                                    </div>
                                </>
                            ) : setting.key === 'MusicProviderFallback' ? (
                                null // Handled in Primary block
                            ) : setting.key === 'BookProvider' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={bookProviders}
                                    placeholder="Select book provider..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'ComicProvider' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={comicProviders}
                                    placeholder="Select primary comic provider..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'ComicFallbackProvider' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={comicFallbackProviders}
                                    placeholder="Select fallback comic provider..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'GameProvider' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={gameProviders}
                                    placeholder="Select game provider..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'PhotoProvider' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={photoProviders}
                                    placeholder="Select photo provider..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'HardwareAcceleration' ? (
                                <Combobox
                                    value={hardwareAccelOptions.find(o => o.value === setting.value)?.label || setting.value}
                                    onChange={(val) => {
                                        const option = hardwareAccelOptions.find(o => o.label === val);
                                        handleChange(setting.key, option?.value || 'none');
                                    }}
                                    options={hardwareAccelOptions.map(o => o.label)}
                                    placeholder="Select hardware acceleration..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'TranscodePreset' ? (
                                <Combobox
                                    value={presetOptions.find(o => o.value === setting.value)?.label || setting.value}
                                    onChange={(val) => {
                                        const option = presetOptions.find(o => o.label === val);
                                        handleChange(setting.key, option?.value || 'veryfast');
                                    }}
                                    options={presetOptions.map(o => o.label)}
                                    placeholder="Select encoding preset..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'MaxTranscodeResolution' ? (
                                <Combobox
                                    value={resolutionOptions.find(o => o.value === setting.value)?.label || setting.value}
                                    onChange={(val) => {
                                        const option = resolutionOptions.find(o => o.label === val);
                                        handleChange(setting.key, option?.value || 'original');
                                    }}
                                    options={resolutionOptions.map(o => o.label)}
                                    placeholder="Select max resolution..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'TranscodeThreadCount' ? (
                                <div className="flex items-center gap-3 max-w-md">
                                    <input
                                        type="number"
                                        min="0"
                                        max="128"
                                        value={setting.value}
                                        onChange={(e) => handleChange(setting.key, e.target.value)}
                                        className="w-24 bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none transition-colors"
                                    />
                                    <span className="text-sm text-gray-400">
                                        {parseInt(setting.value) === 0 ? '(Auto)' : 'threads'}
                                    </span>
                                </div>
                            ) : setting.key === 'TranscodeCRF' ? (
                                <div className="max-w-md">
                                    <div className="flex items-center gap-3">
                                        <span className="text-xs text-gray-400 w-16">Lossless</span>
                                        <div className="flex-1 relative">
                                            <input
                                                type="range"
                                                min="0"
                                                max="51"
                                                value={parseInt(setting.value) || 23}
                                                onChange={(e) => handleChange(setting.key, e.target.value)}
                                                className="w-full h-2 bg-white/10 rounded-lg appearance-none cursor-pointer accent-primary"
                                            />
                                            {/* Following label under slider thumb */}
                                            <div
                                                className="absolute -bottom-6 transform -translate-x-1/2 text-xs text-primary font-medium whitespace-nowrap"
                                                style={{ left: `${getCRFPosition(parseInt(setting.value) || 23)}%` }}
                                            >
                                                {setting.value} ({getCRFLabel(parseInt(setting.value) || 23)})
                                            </div>
                                        </div>
                                        <span className="text-xs text-gray-400 w-12 text-right">Worst</span>
                                    </div>
                                    <p className="text-xs text-gray-500 mt-8">
                                        Lower CRF = better quality but larger file sizes. 23 is a good balance.
                                    </p>
                                </div>

                            ) : setting.key === 'MaxStreamingBitrate' ? (
                                <div className="flex items-center gap-3 max-w-md">
                                    <input
                                        type="number"
                                        min="0"
                                        max="100000"
                                        step="1000"
                                        value={setting.value}
                                        onChange={(e) => handleChange(setting.key, e.target.value)}
                                        className="w-32 bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none transition-colors"
                                    />
                                    <span className="text-sm text-gray-400">
                                        {parseInt(setting.value) === 0 ? 'Unlimited' : `${(parseInt(setting.value) / 1000).toFixed(0)} Mbps`}
                                    </span>
                                </div>
                            ) : setting.key === 'MaxSimultaneousTranscodes' ? (
                                <div className="flex items-center gap-3 max-w-md">
                                    <input
                                        type="number"
                                        min="0"
                                        max="20"
                                        value={setting.value}
                                        onChange={(e) => handleChange(setting.key, e.target.value)}
                                        className="w-24 bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none transition-colors"
                                    />
                                    <span className="text-sm text-gray-400">
                                        {parseInt(setting.value) === 0 ? 'Unlimited' : 'concurrent sessions'}
                                    </span>
                                </div>
                            ) : setting.key === 'DefaultStreamingQuality' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={['auto', '720p', '1080p', '4k', 'original']}
                                    placeholder="Select default quality..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'DefaultAudioChannels' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={['auto', 'stereo', '5.1', '7.1']}
                                    placeholder="Select audio preference..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'ToneMappingAlgorithm' ? (
                                <Combobox
                                    value={toneMappingOptions.find(o => o.value === setting.value)?.label || setting.value}
                                    onChange={(val) => {
                                        const option = toneMappingOptions.find(o => o.label === val);
                                        handleChange(setting.key, option?.value || 'hable');
                                    }}
                                    options={toneMappingOptions.map(o => o.label)}
                                    placeholder="Select tone mapping..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'OutputVideoCodec' ? (
                                <Combobox
                                    value={outputCodecOptions.find(o => o.value === setting.value)?.label || setting.value}
                                    onChange={(val) => {
                                        const option = outputCodecOptions.find(o => o.label === val);
                                        handleChange(setting.key, option?.value || 'auto');
                                    }}
                                    options={outputCodecOptions.map(o => o.label)}
                                    placeholder="Select output codec..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'PreserveHDR' ? (
                                <div className="flex items-center gap-3">
                                    <button
                                        onClick={() => handleChange(setting.key, setting.value === 'true' ? 'false' : 'true')}
                                        className={cn(
                                            "w-12 h-6 rounded-full transition-colors relative",
                                            setting.value === 'true' ? "bg-primary" : "bg-white/10"
                                        )}
                                    >
                                        <div className={cn(
                                            "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                            setting.value === 'true' ? "left-7" : "left-1"
                                        )} />
                                    </button>
                                    <span className="text-sm text-gray-400">{setting.value === 'true' ? 'Enabled' : 'Disabled'}</span>
                                </div>
                            ) : setting.key === 'MetadataRefreshIntervalDays' ? (
                                <div className="flex items-center gap-3 max-w-md">
                                    <input
                                        type="number"
                                        min="0"
                                        max="365"
                                        value={setting.value}
                                        onChange={(e) => handleChange(setting.key, e.target.value)}
                                        className="w-24 bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none transition-colors"
                                    />
                                    <span className="text-sm text-gray-400">
                                        {parseInt(setting.value) === 0 ? '(Disabled)' : 'days'}
                                    </span>
                                </div>
                            ) : setting.key === 'MetadataRefreshMode' ? (
                                <Combobox
                                    value={setting.value}
                                    onChange={(val) => handleChange(setting.key, val)}
                                    options={['Running', 'Variable', 'All']}
                                    placeholder="Select refresh mode..."
                                    className="max-w-md"
                                />
                            ) : setting.key === 'MetadataRefreshOnStartup' ? (
                                <div className="flex items-center gap-3">
                                    <button
                                        onClick={() => handleChange(setting.key, setting.value === 'true' ? 'false' : 'true')}
                                        className={cn(
                                            "w-12 h-6 rounded-full transition-colors relative",
                                            setting.value === 'true' ? "bg-primary" : "bg-white/10"
                                        )}
                                    >
                                        <div className={cn(
                                            "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                            setting.value === 'true' ? "left-7" : "left-1"
                                        )} />
                                    </button>
                                    <span className="text-sm text-gray-400">{setting.value === 'true' ? 'Enabled' : 'Disabled'}</span>
                                </div>
                            ) : (
                                <input
                                    type="text"
                                    value={setting.value}
                                    onChange={(e) => handleChange(setting.key, e.target.value)}
                                    className="w-full max-w-md bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none transition-colors"
                                />
                            )}


                            {setting.description && !['MusicProviderPrimary', 'MusicProviderFallback'].includes(setting.key) && (
                                <p className="text-xs text-gray-500">{setting.description}</p>
                            )}
                        </div>
                    );
                })}
            </div >
        );
    };

    return (
        <div className={cn("mx-auto p-6 pb-24 transition-all duration-300",
            ['users', 'library-metadata-providers', 'playback-transcoding', 'admin'].includes(activeTab) ? "max-w-7xl" : "max-w-4xl")} >
            <div className="flex items-center justify-between mb-8">
                <div className="flex items-center gap-4">
                    <Settings className="w-8 h-8 text-primary" />
                    <h1 className="text-3xl font-bold text-white">
                        {section === 'client' ? t('Client Settings') : t('Settings')}
                    </h1>
                </div>
                {isAdmin && section !== 'client' && (
                    <button
                        onClick={handleSave}
                        disabled={updateMutation.isPending}
                        className="flex items-center gap-2 px-6 py-2 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-colors disabled:opacity-50"
                    >
                        {updateMutation.isPending ? <RefreshCw className="animate-spin" size={18} /> : <Save size={18} />}
                        {t('Save Changes')}
                    </button>
                )}
            </div>

            {/* Content Area */}
            <div className="bg-[#1a1a1a] rounded-2xl border border-white/5 p-8 min-h-[600px]">
                {isLoading && section !== 'client' ? (
                    <div className="flex items-center justify-center h-full text-gray-400">
                        <RefreshCw className="animate-spin mr-2" /> Loading settings...
                    </div>
                ) : (
                    <>
                        {section === 'client' && (
                            <ClientSettings subsection={subsection} />
                        )}

                        {activeTab === 'playback-transcoding' && (
                            <div>
                                <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                    <Play className="text-primary" /> Transcoding
                                </h2>
                                {renderSettingsGroup('Transcoding')}
                            </div>
                        )}

                        {activeTab === 'playback-streaming' && (
                            <div>
                                <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                    <Play className="text-primary" /> Streaming Quality
                                </h2>
                                {renderSettingsGroup('Streaming')}
                            </div>
                        )}


                        {activeTab === 'library-metadata-providers' && (
                            <div className="space-y-8">
                                <h2 className="text-2xl font-bold text-white mb-2 flex items-center gap-3">
                                    <Database className="text-primary" /> Metadata Providers
                                </h2>

                                {/* Metadata Providers Section */}
                                <div>


                                    <div className="space-y-4">
                                        {/* Movie Provider */}
                                        {(() => {
                                            const setting = localSettings.find(s => s.key === 'MovieProvider');
                                            const apiKeyModeSetting = localSettings.find(s => s.key === 'OMDbApiKeyMode');
                                            const customKeySetting = localSettings.find(s => s.key === 'OMDbApiKeyCustom');
                                            if (!setting) return null;

                                            const isOMDb = setting.value === 'OMDb';
                                            const apiKeyMode = apiKeyModeSetting?.value || 'softmedia';

                                            return (
                                                <div className="bg-black/20 rounded-lg p-4 border border-white/5">

                                                    <label className="text-sm font-medium text-gray-300 block mb-2">🎬 Movies</label>
                                                    <Combobox
                                                        value={setting.value}
                                                        onChange={(val) => {
                                                            handleChange(setting.key, val);
                                                            // Set default API key mode when switching to OMDB
                                                            if (val === 'OMDb' && apiKeyModeSetting) {
                                                                handleChange('OMDbApiKeyMode', 'softmedia');
                                                            }
                                                        }}
                                                        options={movieProviders}
                                                        placeholder="Select provider..."
                                                        className="w-full"
                                                    />

                                                    {/* OMDB API Key Options */}
                                                    {isOMDb && apiKeyModeSetting && (
                                                        <div className="mt-4 space-y-3">
                                                            <label className="text-xs text-gray-400 block">API Key</label>
                                                            <Combobox
                                                                value={
                                                                    apiKeyMode === 'softmedia' ? 'SoftMedia Key (Default)' : 'Use My Own Key'
                                                                }
                                                                onChange={(val) => {
                                                                    const mode = val === 'SoftMedia Key (Default)' ? 'softmedia' : 'custom';
                                                                    handleChange('OMDbApiKeyMode', mode);
                                                                }}
                                                                options={['SoftMedia Key (Default)', 'Use My Own Key']}
                                                                placeholder="Select API key mode..."
                                                                className="w-full"
                                                            />

                                                            {/* Custom Key Input */}
                                                            {apiKeyMode === 'custom' && customKeySetting && (
                                                                <div className="mt-2 space-y-3">
                                                                    <input
                                                                        type="password"
                                                                        value={customKeySetting.value}
                                                                        onChange={(e) => handleChange('OMDbApiKeyCustom', e.target.value)}
                                                                        placeholder="Enter your OMDB API key..."
                                                                        className="w-full bg-black/30 border border-white/10 rounded-lg px-3 py-2 text-white text-sm focus:border-primary/50 focus:outline-none"
                                                                    />
                                                                    <p className="text-xs text-gray-500">
                                                                        Get a free key at <a href="https://www.omdbapi.com/apikey.aspx" target="_blank" rel="noopener noreferrer" className="text-blue-400 hover:underline">omdbapi.com</a> and dont forget to activate the API key by clicking the link in the email
                                                                    </p>

                                                                    {/* Tier Dropdown */}
                                                                    {(() => {
                                                                        const tierSetting = localSettings.find(s => s.key === 'OMDbApiTier');
                                                                        const currentTier = tierSetting?.value || 'free';
                                                                        const tierOptions = [
                                                                            { value: 'free', label: 'Free (1,000/day)' },
                                                                            { value: 'basic', label: 'Basic (100,000/day)' },
                                                                            { value: 'standard', label: 'Standard (250,000/day)' },
                                                                            { value: 'pro', label: 'Pro (Unlimited)' }
                                                                        ];
                                                                        return (
                                                                            <div>
                                                                                <label className="text-xs text-gray-400 block mb-1">API Tier</label>
                                                                                <Combobox
                                                                                    value={tierOptions.find(t => t.value === currentTier)?.label || 'Free (1,000/day)'}
                                                                                    onChange={(val) => {
                                                                                        const tier = tierOptions.find(t => t.label === val);
                                                                                        if (tier && tier.value !== 'free' && currentTier === 'free') {
                                                                                            // Show warning for non-free tier
                                                                                            if (confirm('⚠️ Important: Rate Limit Responsibility\n\nSelecting a tier that doesn\'t match your actual OMDb subscription may cause you to exceed limits and get your IP banned by OMDb.\n\nSoftMedia cannot verify your tier or protect against bans.\n\nAre you sure you want to continue?')) {
                                                                                                handleChange('OMDbApiTier', tier.value);
                                                                                            }
                                                                                        } else if (tier) {
                                                                                            handleChange('OMDbApiTier', tier.value);
                                                                                        }
                                                                                    }}
                                                                                    options={tierOptions.map(t => t.label)}
                                                                                    placeholder="Select tier..."
                                                                                    className="w-full"
                                                                                />
                                                                            </div>
                                                                        );
                                                                    })()}
                                                                </div>
                                                            )}

                                                            {/* OMDb Usage Display - Shows for both key modes */}
                                                            {omdbUsage && (
                                                                <div className="mt-4 p-3 rounded-lg bg-black/30 border border-white/10">
                                                                    <div className="flex items-center justify-between mb-2">
                                                                        <span className="text-xs text-gray-400">Daily Usage</span>
                                                                        <span className="text-xs text-gray-500">{omdbUsage.tier.charAt(0).toUpperCase() + omdbUsage.tier.slice(1)} tier</span>
                                                                    </div>
                                                                    <div className="flex items-center gap-3">
                                                                        <div className="flex-1 h-2 bg-black/50 rounded-full overflow-hidden">
                                                                            <div
                                                                                className={`h-full transition-all ${omdbUsage.isExhausted ? 'bg-red-500' :
                                                                                    (omdbUsage.used / omdbUsage.limit) > 0.8 ? 'bg-amber-500' : 'bg-emerald-500'
                                                                                    }`}
                                                                                style={{ width: `${Math.min(100, (omdbUsage.used / omdbUsage.limit) * 100)}%` }}
                                                                            />
                                                                        </div>
                                                                        <span className={`text-sm font-medium ${omdbUsage.isExhausted ? 'text-red-400' :
                                                                            (omdbUsage.used / omdbUsage.limit) > 0.8 ? 'text-amber-400' : 'text-emerald-400'
                                                                            }`}>
                                                                            {omdbUsage.used.toLocaleString()} / {omdbUsage.limit === 999999999 ? '∞' : omdbUsage.limit.toLocaleString()}
                                                                        </span>
                                                                    </div>
                                                                    {omdbUsage.isExhausted && (
                                                                        <p className="text-xs text-red-400 mt-2">⚠️ Daily limit exhausted. Resets at midnight UTC.</p>
                                                                    )}
                                                                </div>
                                                            )}
                                                        </div>
                                                    )}
                                                </div>
                                            );
                                        })()}

                                        {/* TV Provider */}
                                        {(() => {
                                            const setting = localSettings.find(s => s.key === 'TVProvider');
                                            if (!setting) return null;
                                            return (
                                                <div className="bg-black/20 rounded-lg p-4 border border-white/5">
                                                    <label className="text-sm font-medium text-gray-300 block mb-2">📺 TV Shows</label>
                                                    <Combobox
                                                        value={setting.value}
                                                        onChange={(val) => handleChange(setting.key, val)}
                                                        options={tvProviders}
                                                        placeholder="Select provider..."
                                                        className="w-full"
                                                    />
                                                    {setting.value === 'TVMaze' && (
                                                        <p className="text-xs text-blue-300 mt-2">
                                                            Data by <a href="https://www.tvmaze.com/premium" target="_blank" rel="noopener noreferrer" className="underline hover:text-white">TVMaze</a>
                                                        </p>
                                                    )}
                                                </div>
                                            );
                                        })()}

                                        {/* Music Providers */}
                                        {(() => {
                                            const primarySetting = localSettings.find(s => s.key === 'MusicProviderPrimary');
                                            const fallbackSetting = localSettings.find(s => s.key === 'MusicProviderFallback');
                                            if (!primarySetting) return null;
                                            return (
                                                <div className="bg-black/20 rounded-lg p-4 border border-white/5">
                                                    <label className="text-sm font-medium text-gray-300 block mb-3">🎵 Music</label>
                                                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                                        <div>
                                                            <label className="text-xs text-gray-400 block mb-1">Primary</label>
                                                            <Combobox
                                                                value={primarySetting.value}
                                                                onChange={(val) => handleChange(primarySetting.key, val)}
                                                                options={musicProviders}
                                                                placeholder="Select provider..."
                                                                className="w-full"
                                                            />
                                                        </div>
                                                        {fallbackSetting && (
                                                            <div>
                                                                <label className="text-xs text-gray-400 block mb-1">Fallback</label>
                                                                <Combobox
                                                                    value={fallbackSetting.value}
                                                                    onChange={(val) => handleChange(fallbackSetting.key, val)}
                                                                    options={musicProviders}
                                                                    placeholder="Select provider..."
                                                                    className="w-full"
                                                                />
                                                            </div>
                                                        )}
                                                    </div>
                                                </div>
                                            );
                                        })()}

                                        {/* Book Provider */}
                                        {(() => {
                                            const setting = localSettings.find(s => s.key === 'BookProvider');
                                            if (!setting) return null;
                                            return (
                                                <div className="bg-black/20 rounded-lg p-4 border border-white/5">
                                                    <label className="text-sm font-medium text-gray-300 block mb-2">📚 Books</label>
                                                    <Combobox
                                                        value={setting.value}
                                                        onChange={(val) => handleChange(setting.key, val)}
                                                        options={bookProviders}
                                                        placeholder="Select provider..."
                                                        className="w-full"
                                                    />
                                                </div>
                                            );
                                        })()}

                                        {/* Comic Provider (Primary + Fallback) */}
                                        {(() => {
                                            const primary = localSettings.find(s => s.key === 'ComicProvider');
                                            const fallback = localSettings.find(s => s.key === 'ComicFallbackProvider');
                                            if (!primary) return null;
                                            return (
                                                <div className="bg-black/20 rounded-lg p-4 border border-white/5">
                                                    <label className="text-sm font-medium text-gray-300 block mb-2">💬 Comics</label>
                                                    <div className="space-y-2">
                                                        <div>
                                                            <span className="text-xs text-gray-500 block mb-1">Primary</span>
                                                            <Combobox
                                                                value={primary.value}
                                                                onChange={(val) => handleChange(primary.key, val)}
                                                                options={comicProviders}
                                                                placeholder="Select primary..."
                                                                className="w-full"
                                                            />
                                                        </div>
                                                        {fallback && (
                                                            <div>
                                                                <span className="text-xs text-gray-500 block mb-1">Fallback</span>
                                                                <Combobox
                                                                    value={fallback.value}
                                                                    onChange={(val) => handleChange(fallback.key, val)}
                                                                    options={comicFallbackProviders}
                                                                    placeholder="Select fallback..."
                                                                    className="w-full"
                                                                />
                                                            </div>
                                                        )}
                                                    </div>
                                                </div>
                                            );
                                        })()}

                                        {/* Game Provider */}
                                        {(() => {
                                            const setting = localSettings.find(s => s.key === 'GameProvider');
                                            if (!setting) return null;
                                            return (
                                                <div className="bg-black/20 rounded-lg p-4 border border-white/5">
                                                    <label className="text-sm font-medium text-gray-300 block mb-2">🎮 Games</label>
                                                    <Combobox
                                                        value={setting.value}
                                                        onChange={(val) => handleChange(setting.key, val)}
                                                        options={gameProviders}
                                                        placeholder="Select provider..."
                                                        className="w-full"
                                                    />
                                                </div>
                                            );
                                        })()}

                                        {/* Photo Provider */}
                                        {(() => {
                                            const setting = localSettings.find(s => s.key === 'PhotoProvider');
                                            if (!setting) return null;
                                            return (
                                                <div className="bg-black/20 rounded-lg p-4 border border-white/5">
                                                    <label className="text-sm font-medium text-gray-300 block mb-2">📷 Photos</label>
                                                    <Combobox
                                                        value={setting.value}
                                                        onChange={(val) => handleChange(setting.key, val)}
                                                        options={photoProviders}
                                                        placeholder="Select provider..."
                                                        className="w-full"
                                                    />
                                                </div>
                                            );
                                        })()}
                                    </div>
                                </div>
                            </div>
                        )}

                        {activeTab === 'users' && (
                            <div>
                                <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                    <Users className="text-primary" /> Account Management
                                </h2>
                                {renderSettingsGroup('Users')}

                                <div className="mt-8 space-y-8">
                                    <div>
                                        <h3 className="text-lg font-semibold text-white mb-4">Users</h3>
                                        <UserListTable />
                                    </div>

                                    <div className="border-t border-white/5 pt-8">
                                        <InviteManager />
                                    </div>
                                </div>
                            </div>
                        )}

                        {activeTab === 'library-libraries' && (
                            <div>
                                <div className="flex items-center justify-between mb-6">
                                    <h2 className="text-2xl font-bold text-white flex items-center gap-3">
                                        <LibraryIcon className="text-primary" /> Libraries
                                    </h2>
                                </div>

                                {/* Scan Status Panel */}
                                <div className="mb-6 p-4 bg-white/5 rounded-xl border border-white/10">
                                    <h3 className="text-sm font-medium text-gray-400 uppercase tracking-wide mb-3">Scan Status</h3>

                                    {/* Active/Queued Scans */}
                                    {scanQueue.filter(j => j.status === 'Running' || j.status === 'Queued').length > 0 ? (
                                        <div className="space-y-3 mb-4">
                                            {scanQueue
                                                .filter(j => j.status === 'Running' || j.status === 'Queued')
                                                .map(job => (
                                                    <LibraryScanProgress key={job.id} job={job} />
                                                ))
                                            }
                                        </div>
                                    ) : (
                                        <p className="text-sm text-gray-500 mb-4">No active scans. Click the refresh icon on a library to start a scan.</p>
                                    )}

                                    {/* Recent Completed/Failed Scans */}
                                    {scanQueue.filter(j => j.status === 'Completed' || j.status === 'Failed').length > 0 && (
                                        <div className="border-t border-white/10 pt-3 mt-3">
                                            <h4 className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">Recent Scans</h4>
                                            <div className="space-y-2">
                                                {/* Show only the most recent completed/failed scan per library */}
                                                {Object.values(
                                                    scanQueue
                                                        .filter(j => j.status === 'Completed' || j.status === 'Failed')
                                                        .reduce((acc, job) => {
                                                            // Keep only the most recent scan per library
                                                            const existing = acc[job.libraryId];
                                                            if (!existing ||
                                                                (job.completedAt && (!existing.completedAt ||
                                                                    job.completedAt > existing.completedAt))) {
                                                                acc[job.libraryId] = job;
                                                            }
                                                            return acc;
                                                        }, {} as Record<string, typeof scanQueue[0]>)
                                                ).map(job => (
                                                    <LibraryScanProgress key={job.id} job={job} compact />
                                                ))}
                                            </div>
                                        </div>
                                    )}
                                </div>

                                {/* Add Library Button */}
                                <div className="flex justify-end mb-4">
                                    <button
                                        onClick={() => {
                                            setEditingLibrary(undefined);
                                            setIsLibraryFormOpen(true);
                                        }}
                                        className="flex items-center gap-2 px-4 py-2 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-colors"
                                    >
                                        <Plus size={18} />
                                        Add Library
                                    </button>
                                </div>

                                {/* Libraries List */}
                                {isLoadingLibraries ? (
                                    <div className="text-center py-12">
                                        <RefreshCw className="animate-spin w-8 h-8 text-primary mx-auto" />
                                    </div>
                                ) : (
                                    <LibraryListTable
                                        libraries={libraries || []}
                                        scanJobs={scanQueue}
                                        onEdit={(library) => {
                                            setEditingLibrary(library);
                                            setIsLibraryFormOpen(true);
                                        }}
                                        onDelete={(library) => setLibraryToDelete(library)}
                                        onReorder={(orderedIds) => reorderLibraryMutation.mutate(orderedIds)}
                                        onScan={(library) => scanLibraryMutation.mutate(library.id)}
                                    />
                                )}

                                {/* Scanning Settings (File Watcher etc) */}
                                <div className="mt-8 pt-8 border-t border-white/5">
                                    <h3 className="text-lg font-semibold text-white mb-4">Scanning Settings</h3>
                                    {renderSettingsGroup('Scanning')}
                                </div>
                            </div>
                        )}

                        {activeTab === 'admin' && (
                            <AdminDashboard />
                        )}
                    </>
                )}
            </div>

            {isLibraryFormOpen && (
                <LibraryForm
                    initialData={editingLibrary}
                    onSubmit={handleLibrarySubmit}
                    onCancel={() => {
                        setIsLibraryFormOpen(false);
                        setEditingLibrary(undefined);
                    }}
                    isLoading={createLibraryMutation.isPending || updateLibraryMutation.isPending}
                />
            )}

            <ConfirmationModal
                isOpen={!!libraryToDelete}
                title="Delete Library"
                message={`Are you sure you want to delete "${libraryToDelete?.name}"? This action cannot be undone.`}
                onConfirm={() => {
                    if (libraryToDelete) {
                        deleteLibraryMutation.mutate(libraryToDelete.id);
                        setLibraryToDelete(null);
                    }
                }}
                onCancel={() => setLibraryToDelete(null)}
                variant="danger"
            />
        </div>
    );
}

