import { Globe, Volume2, Wifi } from 'lucide-react';
import { useLocalPreferences } from '../../hooks/useLocalPreferences';
import { cn } from '../../lib/utils';

interface ClientSettingsProps {
    subsection?: string;
}

export default function ClientSettings({ subsection = 'general' }: ClientSettingsProps) {
    // Local preferences (Device-specific & User-isolated)
    const { preferences: localPrefs, updatePreference: updateLocalPref } = useLocalPreferences();

    if (subsection === 'playback') {
        return (
            <div className="space-y-6">
                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <div className="flex items-center gap-3 mb-6">
                        <Wifi className="w-5 h-5 text-purple-400" />
                        <h2 className="text-lg font-semibold text-white">Streaming Quality</h2>
                        <span className="text-xs bg-purple-500/20 text-purple-400 px-2 py-0.5 rounded-full">This device</span>
                    </div>

                    <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                        <div className="flex flex-col gap-2">
                            <label className="text-sm font-medium text-gray-400">Default Quality</label>
                            <select
                                value={localPrefs.defaultStreamingQuality}
                                onChange={(e) => updateLocalPref('defaultStreamingQuality', e.target.value)}
                                className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                            >
                                <option value="auto">Auto</option>
                                <option value="720p">720p</option>
                                <option value="1080p">1080p</option>
                                <option value="4k">4K</option>
                                <option value="original">Original</option>
                            </select>
                        </div>

                        <div className="flex flex-col gap-2">
                            <label className="text-sm font-medium text-gray-400">Max Bitrate (kbps)</label>
                            <select
                                value={localPrefs.maxBitrate}
                                onChange={(e) => updateLocalPref('maxBitrate', e.target.value)}
                                className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                            >
                                <option value="0">Unlimited</option>
                                <option value="2000">2 Mbps</option>
                                <option value="4000">4 Mbps</option>
                                <option value="8000">8 Mbps</option>
                                <option value="20000">20 Mbps</option>
                                <option value="40000">40 Mbps</option>
                            </select>
                        </div>

                        <div className="flex items-center justify-between bg-white/5 rounded-lg px-4 py-3 md:col-span-2">
                            <div className="flex items-center gap-3">
                                <Volume2 className="w-4 h-4 text-gray-400" />
                                <div>
                                    <span className="text-white text-sm">Data Saver Mode</span>
                                    <p className="text-xs text-gray-500">Reduce quality on mobile data</p>
                                </div>
                            </div>
                            <button
                                onClick={() => updateLocalPref('dataSaverMode',
                                    localPrefs.dataSaverMode === 'true' ? 'false' : 'true'
                                )}
                                className={cn(
                                    "w-12 h-6 rounded-full transition-colors relative",
                                    localPrefs.dataSaverMode === 'true' ? "bg-purple-500" : "bg-white/20"
                                )}
                            >
                                <div className={cn(
                                    "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                    localPrefs.dataSaverMode === 'true' ? "left-7" : "left-1"
                                )} />
                            </button>
                        </div>
                    </div>
                </div>
            </div>
        );
    }

    return (
        <div className="space-y-6">
            <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                <div className="flex items-center gap-3 mb-6">
                    <Globe className="w-5 h-5 text-primary" />
                    <h2 className="text-lg font-semibold text-white">Language & Subtitles</h2>
                    <span className="text-xs bg-primary/20 text-primary px-2 py-0.5 rounded-full">This Device</span>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                    <div className="flex flex-col gap-2">
                        <label className="text-sm font-medium text-gray-400">Audio Language</label>
                        <select
                            value={localPrefs.audioLanguage}
                            onChange={(e) => updateLocalPref('audioLanguage', e.target.value)}
                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                        >
                            <option value="en">English</option>
                            <option value="es">Spanish</option>
                            <option value="fr">French</option>
                            <option value="de">German</option>
                            <option value="it">Italian</option>
                            <option value="pt">Portuguese</option>
                            <option value="ja">Japanese</option>
                            <option value="zh">Chinese</option>
                            <option value="ar">Arabic</option>
                            <option value="ru">Russian</option>
                            <option value="pl">Polish</option>
                            <option value="tr">Turkish</option>
                            <option value="sv">Swedish</option>
                            <option value="original">Original</option>
                        </select>
                    </div>

                    <div className="flex flex-col gap-2">
                        <label className="text-sm font-medium text-gray-400">Subtitle Language</label>
                        <select
                            value={localPrefs.subtitleLanguage}
                            onChange={(e) => updateLocalPref('subtitleLanguage', e.target.value)}
                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                        >
                            <option value="off">Off</option>
                            <option value="en">English</option>
                            <option value="es">Spanish</option>
                            <option value="fr">French</option>
                            <option value="de">German</option>
                            <option value="it">Italian</option>
                            <option value="pt">Portuguese</option>
                            <option value="ja">Japanese</option>
                            <option value="zh">Chinese</option>
                            <option value="ar">Arabic</option>
                            <option value="ru">Russian</option>
                            <option value="pl">Polish</option>
                            <option value="tr">Turkish</option>
                            <option value="sv">Swedish</option>
                        </select>
                    </div>

                </div>
            </div>
        </div>
    );
}
