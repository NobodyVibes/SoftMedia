import { useState, useEffect } from 'react';
import { Settings, Server, Users, Library as LibraryIcon, Save, RefreshCw, Database, Network, Play, Plus } from 'lucide-react';
import { cn } from '../lib/utils';
import { Combobox } from '../components/ui/Combobox';
import { settingsService, type AppSetting } from '../services/settingsService';
import { libraryService } from '../services/libraryService';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { UserListTable } from '../components/UserListTable';
import { InviteManager } from '../components/InviteManager';
import { LibraryListTable } from '../components/LibraryListTable';
import { LibraryForm } from '../components/LibraryForm';
import { ConfirmationModal } from '../components/ConfirmationModal';
import type { Library } from '../types';

type Tab = 'server' | 'users' | 'libraries' | 'metadata' | 'playback' | 'network';

export default function SettingsPage() {
    const [activeTab, setActiveTab] = useState<Tab>('server');
    const queryClient = useQueryClient();
    const [localSettings, setLocalSettings] = useState<AppSetting[]>([]);
    const { t, i18n } = useTranslation();

    // Library State
    const [isLibraryFormOpen, setIsLibraryFormOpen] = useState(false);
    const [editingLibrary, setEditingLibrary] = useState<Library | undefined>(undefined);
    const [libraryToDelete, setLibraryToDelete] = useState<Library | null>(null);

    // Fetch Settings
    const { data: settings, isLoading } = useQuery({
        queryKey: ['settings'],
        queryFn: settingsService.getAll,
    });

    // Fetch Libraries
    const { data: libraries, isLoading: isLoadingLibraries } = useQuery({
        queryKey: ['libraries'],
        queryFn: libraryService.getAll,
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
        onError: () => {
            toast.error(t('Failed to create library'));
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
        onSuccess: () => {
            toast.success('Library scan started');
        },
        onError: () => toast.error('Failed to start library scan'),
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

    const movieProviders = ["Wikidata"];
    const tvProviders = ["TVMaze"];
    const musicProviders = ["MusicBrainz", "Embedded"];
    const bookProviders = ["Open Library"];
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

    const tabs = [
        { id: 'server', label: t('Server'), icon: Server },
        { id: 'network', label: t('Network'), icon: Network },
        { id: 'playback', label: t('Playback'), icon: Play },
        { id: 'metadata', label: t('Metadata'), icon: Database },
        { id: 'users', label: t('Users'), icon: Users },
        { id: 'libraries', label: t('Libraries'), icon: LibraryIcon },
    ];

    // Helper to render settings by group
    const renderSettingsGroup = (groupName: string) => {
        let groupSettings = localSettings.filter(s => s.group === groupName);

        // Explicit ordering for Transcoding group
        if (groupName === 'Transcoding') {
            const transcodingOrder = ['HardwareAcceleration', 'TranscodePreset', 'TranscodeThreadCount', 'MaxTranscodeResolution', 'TranscodeCRF', 'DisableTranscoding'];
            groupSettings = groupSettings.sort((a, b) => {
                const aIndex = transcodingOrder.indexOf(a.key);
                const bIndex = transcodingOrder.indexOf(b.key);
                return (aIndex === -1 ? 999 : aIndex) - (bIndex === -1 ? 999 : bIndex);
            });
        }

        if (groupSettings.length === 0) return <p className="text-gray-500 italic">No settings available.</p>;

        return (
            <div className="space-y-6">
                {groupSettings.map(setting => (
                    <div key={setting.key} className="flex flex-col gap-2">
                        {!['MusicProviderPrimary', 'MusicProviderFallback', 'DisableTranscoding'].includes(setting.key) && (
                            <label className="text-sm font-medium text-gray-300">{t(setting.key.replace(/([A-Z])/g, ' $1').trim())}</label>
                        )}

                        {setting.key === 'AllowUserSignup' ? (
                            <Combobox
                                value={setting.value === 'true' ? 'Enabled' : setting.value === 'false' ? 'Disabled' : setting.value}
                                onChange={(val) => handleChange(setting.key, val)}
                                options={["Disabled", "InviteOnly", "Enabled"]}
                                placeholder="Select signup mode..."
                                className="max-w-md"
                            />
                        ) : (setting.value === 'true' || setting.value === 'false') && setting.key !== 'DisableTranscoding' ? (
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
                        ) : setting.key === 'DisableTranscoding' ? (
                            <div>
                                <label className="flex items-center gap-3 cursor-pointer">
                                    <input
                                        type="checkbox"
                                        checked={setting.value === 'true'}
                                        onChange={(e) => handleChange(setting.key, e.target.checked ? 'true' : 'false')}
                                        className="w-5 h-5 rounded border-white/20 bg-black/20 text-primary focus:ring-primary focus:ring-offset-0 cursor-pointer"
                                    />
                                    <span className="text-sm font-medium text-gray-300">Disable Transcoding</span>
                                </label>
                                <p className="text-xs text-gray-500 mt-2 ml-8">
                                    Skip video conversion and serve files directly. May cause playback issues in browsers that don't support the original video format.
                                </p>
                            </div>
                        ) : (
                            <input
                                type="text"
                                value={setting.value}
                                onChange={(e) => handleChange(setting.key, e.target.value)}
                                className="w-full max-w-md bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none transition-colors"
                            />
                        )}

                        {setting.description && !['MusicProviderPrimary', 'MusicProviderFallback', 'DisableTranscoding'].includes(setting.key) && (
                            <p className="text-xs text-gray-500">{setting.description}</p>
                        )}
                    </div>
                ))
                }
            </div >
        );
    };

    return (
        <div className="p-8 max-w-6xl mx-auto pb-24" >
            <div className="flex items-center justify-between mb-8">
                <div className="flex items-center gap-4">
                    <Settings className="w-8 h-8 text-primary" />
                    <h1 className="text-3xl font-bold text-white">{t('Settings')}</h1>
                </div>
                <button
                    onClick={handleSave}
                    disabled={updateMutation.isPending}
                    className="flex items-center gap-2 px-6 py-2 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-colors disabled:opacity-50"
                >
                    {updateMutation.isPending ? <RefreshCw className="animate-spin" size={18} /> : <Save size={18} />}
                    {t('Save Changes')}
                </button>
            </div>

            <div className="flex flex-col lg:flex-row gap-8">
                {/* Sidebar Tabs */}
                <div className="w-full lg:w-64 flex-shrink-0 space-y-2">
                    {tabs.map((tab) => {
                        const Icon = tab.icon;
                        return (
                            <button
                                key={tab.id}
                                onClick={() => setActiveTab(tab.id as Tab)}
                                className={cn(
                                    "w-full flex items-center gap-3 px-4 py-3 rounded-xl transition-all font-medium text-left",
                                    activeTab === tab.id
                                        ? "bg-primary/10 text-primary border border-primary/20"
                                        : "text-gray-400 hover:bg-white/5 hover:text-white border border-transparent"
                                )}
                            >
                                <Icon size={20} />
                                {tab.label}
                            </button>
                        );
                    })}
                </div>

                {/* Content Area */}
                <div className="flex-1 bg-[#1a1a1a] rounded-2xl border border-white/5 p-8 min-h-[600px]">
                    {isLoading ? (
                        <div className="flex items-center justify-center h-full text-gray-400">
                            <RefreshCw className="animate-spin mr-2" /> Loading settings...
                        </div>
                    ) : (
                        <>
                            {activeTab === 'server' && (
                                <div>
                                    <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                        <Server className="text-primary" /> Server Configuration
                                    </h2>
                                    {renderSettingsGroup('Server')}
                                </div>
                            )}

                            {activeTab === 'network' && (
                                <div>
                                    <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                        <Network className="text-primary" /> Network Settings
                                    </h2>
                                    {renderSettingsGroup('Network')}
                                </div>
                            )}

                            {activeTab === 'playback' && (
                                <div>
                                    <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                        <Play className="text-primary" /> Playback & Transcoding
                                    </h2>
                                    <div className="space-y-8">
                                        <div>
                                            <h3 className="text-lg font-semibold text-white mb-4">Transcoding</h3>
                                            {renderSettingsGroup('Transcoding')}
                                        </div>
                                        <div className="border-t border-white/5 pt-6">
                                            <h3 className="text-lg font-semibold text-white mb-4">Subtitles</h3>
                                            {renderSettingsGroup('Subtitles')}
                                        </div>
                                    </div>
                                </div>
                            )}

                            {activeTab === 'metadata' && (
                                <div>
                                    <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                        <Database className="text-primary" /> Metadata Providers
                                    </h2>
                                    {renderSettingsGroup('Metadata')}
                                </div>
                            )}

                            {activeTab === 'users' && (
                                <div>
                                    <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                        <Users className="text-primary" /> User Management
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

                            {activeTab === 'libraries' && (
                                <div>
                                    <div className="flex items-center justify-between mb-6">
                                        <h2 className="text-2xl font-bold text-white flex items-center gap-3">
                                            <LibraryIcon className="text-primary" /> Library Management
                                        </h2>
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

                                    {isLoadingLibraries ? (
                                        <div className="text-center py-12">
                                            <RefreshCw className="animate-spin w-8 h-8 text-primary mx-auto" />
                                        </div>
                                    ) : (
                                        <LibraryListTable
                                            libraries={libraries || []}
                                            onEdit={(library) => {
                                                setEditingLibrary(library);
                                                setIsLibraryFormOpen(true);
                                            }}
                                            onDelete={(library) => setLibraryToDelete(library)}
                                            onReorder={(orderedIds) => reorderLibraryMutation.mutate(orderedIds)}
                                            onScan={(library) => scanLibraryMutation.mutate(library.id)}
                                        />
                                    )}
                                </div>
                            )}
                        </>
                    )}
                </div>
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
        </div >
    );
}
