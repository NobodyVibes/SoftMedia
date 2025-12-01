import { useState, useEffect } from 'react';
import { Settings, Server, Users, Library, Save, RefreshCw, Database, Network, Play } from 'lucide-react';
import { cn } from '../lib/utils';
import { Combobox } from '../components/ui/Combobox';
import { settingsService, type AppSetting } from '../services/settingsService';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { toast } from 'sonner';
import { useTranslation } from 'react-i18next';
import { UserListTable } from '../components/UserListTable';
import { InviteManager } from '../components/InviteManager';

type Tab = 'server' | 'users' | 'libraries' | 'metadata' | 'playback' | 'network';

export default function SettingsPage() {
    const [activeTab, setActiveTab] = useState<Tab>('server');
    const queryClient = useQueryClient();
    const [localSettings, setLocalSettings] = useState<AppSetting[]>([]);
    const { t, i18n } = useTranslation();

    // Fetch Settings
    const { data: settings, isLoading } = useQuery({
        queryKey: ['settings'],
        queryFn: settingsService.getAll,
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
                // Map full locale codes to short codes if necessary, or ensure i18n resources match
                // For now, assuming direct match or fallback
                i18n.changeLanguage(langSetting.value);
            }
        },
        onError: () => {
            toast.error(t('Failed to save settings'));
        }
    });

    const handleSave = () => {
        updateMutation.mutate(localSettings);
    };

    const handleChange = (key: string, value: string) => {
        setLocalSettings(prev => prev.map(s => s.key === key ? { ...s, value } : s));
    };

    const languageOptions = [
        "en-US", "es-ES", "fr-FR", "de-DE", "it-IT", "pt-BR", "ja-JP", "zh-CN", "ru-RU"
    ];

    const logLevelOptions = [
        "Trace", "Debug", "Info", "Warning", "Error", "Critical"
    ];

    const movieProviders = ["Wikidata"];
    const tvProviders = ["TVMaze"];
    const musicProviders = ["MusicBrainz"];
    const bookProviders = ["Open Library"];
    const gameProviders = ["Wikidata"];
    const photoProviders = ["Exif"];

    const tabs = [
        { id: 'server', label: t('Server'), icon: Server },
        { id: 'network', label: t('Network'), icon: Network },
        { id: 'playback', label: t('Playback'), icon: Play },
        { id: 'metadata', label: t('Metadata'), icon: Database },
        { id: 'users', label: t('Users'), icon: Users },
        { id: 'libraries', label: t('Libraries'), icon: Library },
    ];

    // Helper to render settings by group
    const renderSettingsGroup = (groupName: string) => {
        const groupSettings = localSettings.filter(s => s.group === groupName);

        if (groupSettings.length === 0) return <p className="text-gray-500 italic">No settings available.</p>;

        return (
            <div className="space-y-6">
                {groupSettings.map(setting => (
                    <div key={setting.key} className="flex flex-col gap-2">
                        <label className="text-sm font-medium text-gray-300">{t(setting.key.replace(/([A-Z])/g, ' $1').trim())}</label>

                        {setting.value === 'true' || setting.value === 'false' ? (
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
                            <Combobox
                                value={setting.value}
                                onChange={(val) => handleChange(setting.key, val)}
                                options={tvProviders}
                                placeholder="Select TV provider..."
                                className="max-w-md"
                            />
                        ) : setting.key === 'MusicProvider' ? (
                            <Combobox
                                value={setting.value}
                                onChange={(val) => handleChange(setting.key, val)}
                                options={musicProviders}
                                placeholder="Select music provider..."
                                className="max-w-md"
                            />
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
                        ) : (
                            <input
                                type="text"
                                value={setting.value}
                                onChange={(e) => handleChange(setting.key, e.target.value)}
                                className="w-full max-w-md bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none transition-colors"
                            />
                        )}

                        {setting.description && (
                            <p className="text-xs text-gray-500">{setting.description}</p>
                        )}
                    </div>
                ))
                }
            </div >
        );
    };

    return (
        <div className="p-8 max-w-6xl mx-auto pb-24">
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
                                    <h2 className="text-2xl font-bold text-white mb-6 flex items-center gap-3">
                                        <Library className="text-primary" /> Library Management
                                    </h2>
                                    <div className="p-4 rounded-lg bg-green-500/10 border border-green-500/20 text-green-200">
                                        Library management is currently handled via the database. UI coming soon.
                                    </div>
                                </div>
                            )}
                        </>
                    )}
                </div>
            </div>
        </div>
    );
}
