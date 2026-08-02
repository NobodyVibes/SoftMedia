import { useState } from 'react';
import { Globe, Lightbulb, Music, Volume2, Wifi } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { useLocalPreferences } from '../../hooks/useLocalPreferences';
import { cn } from '../../lib/utils';
import { SUBTITLE_COLORS } from '../../components/player/subtitleStyle';
import { Modal } from '../../components/ui/Modal';
import { accountService, type StreamingLimitsTierDto } from '../../services/accountService';

interface ClientSettingsProps {
    subsection?: string;
}

// QS-WI-009 — display formatting for the read-only "what the server allows you" line.
// 0 means unlimited for both fields (same convention as the server DTO).
function formatTier(tier: StreamingLimitsTierDto): string {
    const bitrate = tier.maxBitrateKbps > 0
        ? `up to ${tier.maxBitrateKbps % 1000 === 0 ? tier.maxBitrateKbps / 1000 : (tier.maxBitrateKbps / 1000).toFixed(1)} Mbps`
        : 'unlimited bitrate';
    const resolution = tier.maxResolution > 0
        ? (tier.maxResolution === 2160 ? '4K' : tier.maxResolution === 4320 ? '8K' : `${tier.maxResolution}p`)
        : 'any resolution';
    return `${bitrate}, ${resolution}`;
}

export default function ClientSettings({ subsection = 'general' }: ClientSettingsProps) {
    // Local preferences (Device-specific & User-isolated)
    const { preferences: localPrefs, updatePreference: updateLocalPref } = useLocalPreferences();

    // QS-WI-011 — disabling Media Tips requires the confirm dialog FIRST (owner decision).
    const [showTipsConfirm, setShowTipsConfirm] = useState(false);
    const mediaTipsOn = localPrefs.mediaTipsEnabled !== 'false';

    // QS-WI-009 — the read-only server-side ceilings ("what the server allows you").
    const { data: serverLimits, isError: serverLimitsFailed } = useQuery({
        queryKey: ['me', 'streaming-limits'],
        queryFn: accountService.getStreamingLimits,
        enabled: subsection === 'playback',
        staleTime: 60_000,
    });

    const handleMediaTipsClick = () => {
        if (mediaTipsOn) {
            setShowTipsConfirm(true);
            return;
        }
        // Re-enabling the group resets the finer-grained per-prompt "Never show again"
        // flags (QS-WI-011) — today that is the HDR guardrail's.
        updateLocalPref('mediaTipsEnabled', 'true');
        updateLocalPref('showHdrTranscodeWarning', 'true');
    };

    if (subsection === 'playback') {
        return (
            <div className="space-y-6">
                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <div className="flex items-center gap-3 mb-2">
                        <Wifi className="w-5 h-5 text-purple-400" />
                        <h2 className="text-lg font-semibold text-white">Streaming Quality</h2>
                        <span className="text-xs bg-purple-500/20 text-purple-400 px-2 py-0.5 rounded-full">This device</span>
                    </div>
                    {/* QS-WI-009 — the model in one line: the client asks, the server clamps. */}
                    <p className="text-sm text-gray-400 mb-2">
                        What this device asks for. The server applies its own limits on top — see below what your account allows.
                    </p>
                    {/* Informational only — the limits are enforced server-side regardless, so
                        when the endpoint is unreachable the line disappears instead of lying
                        or sitting on "checking…" forever. */}
                    {!serverLimitsFailed && (
                        <p className="text-xs text-gray-500 mb-6">
                            {serverLimits
                                ? `What the server allows you — at home: ${formatTier(serverLimits.lan)} · away: ${formatTier(serverLimits.remote)}. Set by your administrator.`
                                : 'What the server allows you: checking…'}
                        </p>
                    )}

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
                            {/* QS-WI-008 — the trustworthy-Auto sentence (plan wording, single-rendition reality). */}
                            <p className="text-xs text-gray-500">
                                Auto: the server picks direct play or remux when possible, else one transcode at
                                the session's effective cap — no client-side bandwidth guessing, ever. If a stream
                                buffers, use the player's Quality menu; "Why is this playing this way?" (in the
                                player's More menu) explains what the stream is doing.
                            </p>
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

                        <div className="flex flex-col gap-2">
                            <label className="text-sm font-medium text-gray-400">Burn Subtitles</label>
                            <select
                                value={localPrefs.burnSubtitles}
                                onChange={(e) => updateLocalPref('burnSubtitles', e.target.value)}
                                className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                            >
                                <option value="auto">Automatic</option>
                                <option value="always">Always Burn-in</option>
                            </select>
                            <p className="text-xs text-gray-500">Force server to burn subtitles into video</p>
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

                        {/* Auto-skip intros (per-device preference) */}
                        <div className="flex items-center justify-between bg-white/5 rounded-lg px-4 py-3 md:col-span-2">
                            <div>
                                <span className="text-white text-sm">Auto-Skip Intros</span>
                                <p className="text-xs text-gray-500">Automatically skip detected intros instead of showing the Skip button.</p>
                            </div>
                            <button
                                type="button"
                                role="switch"
                                aria-checked={localPrefs.autoSkipIntros === 'true'}
                                aria-label="Auto-skip intros"
                                onClick={() => updateLocalPref('autoSkipIntros',
                                    localPrefs.autoSkipIntros === 'true' ? 'false' : 'true'
                                )}
                                className={cn(
                                    "w-12 h-6 rounded-full transition-colors relative focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                    localPrefs.autoSkipIntros === 'true' ? "bg-purple-500" : "bg-white/20"
                                )}
                            >
                                <div className={cn(
                                    "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                    localPrefs.autoSkipIntros === 'true' ? "left-7" : "left-1"
                                )} />
                            </button>
                        </div>

                        {/* Auto-skip credits (per-device preference) */}
                        <div className="flex items-center justify-between bg-white/5 rounded-lg px-4 py-3 md:col-span-2">
                            <div>
                                <span className="text-white text-sm">Auto-Skip Credits</span>
                                <p className="text-xs text-gray-500">Automatically skip end credits when detected.</p>
                            </div>
                            <button
                                type="button"
                                role="switch"
                                aria-checked={localPrefs.autoSkipCredits === 'true'}
                                aria-label="Auto-skip credits"
                                onClick={() => updateLocalPref('autoSkipCredits',
                                    localPrefs.autoSkipCredits === 'true' ? 'false' : 'true'
                                )}
                                className={cn(
                                    "w-12 h-6 rounded-full transition-colors relative focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                    localPrefs.autoSkipCredits === 'true' ? "bg-purple-500" : "bg-white/20"
                                )}
                            >
                                <div className={cn(
                                    "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                    localPrefs.autoSkipCredits === 'true' ? "left-7" : "left-1"
                                )} />
                            </button>
                        </div>

                        {/* Photo slideshow transition (per-device preference) */}
                        <div className="space-y-2 md:col-span-2">
                            <label className="text-white text-sm">Photo Slideshow Transition</label>
                            <select
                                value={localPrefs.slideshowTransition}
                                onChange={(e) => updateLocalPref('slideshowTransition', e.target.value)}
                                className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                            >
                                <option value="fade">Fade</option>
                                <option value="zoom">Zoom (slow Ken Burns drift)</option>
                                <option value="slide">Slide</option>
                                <option value="none">None (instant)</option>
                            </select>
                            <p className="text-xs text-gray-500">How photos enter in the photo viewer and slideshow.</p>
                        </div>
                    </div>
                </div>

                {/* QS-WI-011 — Media Tips: ONE device-local toggle for unsolicited playback
                    tips (today: the pre-play HDR conversion warning). User-invoked
                    diagnostics ("Why is this playing this way?") and admin rules (blocked
                    HDR transcodes) are never affected. */}
                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <div className="flex items-center gap-3 mb-6">
                        <Lightbulb className="w-5 h-5 text-purple-400" />
                        <h2 className="text-lg font-semibold text-white">Media Tips</h2>
                        <span className="text-xs bg-purple-500/20 text-purple-400 px-2 py-0.5 rounded-full">This device</span>
                    </div>

                    <div className="flex items-center justify-between bg-white/5 rounded-lg px-4 py-3">
                        <div>
                            <span className="text-white text-sm">Show Media Tips</span>
                            <p className="text-xs text-gray-500">
                                Proactive playback tips, like the warning before an HDR video is converted
                                to SDR and similar in-player notices. Turning tips back on also restores
                                any "Never show again" choices.
                            </p>
                        </div>
                        <button
                            type="button"
                            role="switch"
                            aria-checked={mediaTipsOn}
                            aria-label="Show Media Tips"
                            onClick={handleMediaTipsClick}
                            className={cn(
                                "w-12 h-6 rounded-full transition-colors relative focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400",
                                mediaTipsOn ? "bg-purple-500" : "bg-white/20"
                            )}
                        >
                            <div className={cn(
                                "absolute top-1 w-4 h-4 rounded-full bg-white transition-all",
                                mediaTipsOn ? "left-7" : "left-1"
                            )} />
                        </button>
                    </div>
                </div>

                {/* QS-WI-011 — the disable-confirm dialog (owner wording). closeOnBackdrop off
                    so a stray click can't count as an answer. */}
                <Modal
                    isOpen={showTipsConfirm}
                    onClose={() => setShowTipsConfirm(false)}
                    title="Turn off Media Tips?"
                    closeOnBackdrop={false}
                >
                    <div className="space-y-4">
                        <p className="text-sm text-gray-300">
                            Streaming and transcoding are complex, and most people don't realize what
                            affects their playback. Leaving Media Tips on helps you diagnose hardware
                            resource usage and playback quality issues as they happen.
                        </p>
                        <p className="text-sm text-gray-400">
                            You can always ask for an explanation yourself: "Why is this playing this
                            way?" in the player's More menu stays available either way.
                        </p>
                        <div className="flex justify-end gap-3 pt-2">
                            <button
                                type="button"
                                onClick={() => setShowTipsConfirm(false)}
                                className="px-4 py-2 rounded bg-primary hover:bg-primary/90 text-white transition-colors"
                            >
                                Keep tips on
                            </button>
                            <button
                                type="button"
                                onClick={() => {
                                    updateLocalPref('mediaTipsEnabled', 'false');
                                    setShowTipsConfirm(false);
                                }}
                                className="px-4 py-2 rounded text-gray-300 hover:bg-gray-700 transition-colors"
                            >
                                Turn off
                            </button>
                        </div>
                    </div>
                </Modal>
            </div>
        );
    }

    if (subsection === 'audio') {
        return (
            <div className="space-y-6">
                <div className="bg-white/5 rounded-xl p-6 border border-white/10">
                    <div className="flex items-center gap-3 mb-6">
                        <Music className="w-5 h-5 text-purple-400" />
                        <h2 className="text-lg font-semibold text-white">Audio Playback</h2>
                        <span className="text-xs bg-purple-500/20 text-purple-400 px-2 py-0.5 rounded-full">This device</span>
                    </div>

                    <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                        <div className="flex flex-col gap-2">
                            <label className="text-sm font-medium text-gray-400">Max Audio Quality</label>
                            <select
                                value={localPrefs.maxAudioBitrate}
                                onChange={(e) => updateLocalPref('maxAudioBitrate', e.target.value)}
                                className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                            >
                                <option value="0">Original (Lossless)</option>
                                <option value="320">320 kbps (High)</option>
                                <option value="256">256 kbps</option>
                                <option value="192">192 kbps</option>
                                <option value="128">128 kbps (Low)</option>
                            </select>
                            <p className="text-xs text-gray-500">Lower bitrate saves bandwidth when transcoding is required</p>
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

                    {/* R-WI-018 — caption appearance (applies to the player's text
                        subtitles; burned-in subtitles are rendered by the server) */}
                    <div className="flex flex-col gap-2">
                        <label className="text-sm font-medium text-gray-400">Caption Size</label>
                        <select
                            value={localPrefs.subtitleFontSize}
                            onChange={(e) => updateLocalPref('subtitleFontSize', e.target.value)}
                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                        >
                            <option value="75">Small</option>
                            <option value="100">Normal</option>
                            <option value="125">Large</option>
                            <option value="150">Extra Large</option>
                        </select>
                    </div>

                    <div className="flex flex-col gap-2">
                        <label className="text-sm font-medium text-gray-400">Caption Color</label>
                        <select
                            value={localPrefs.subtitleColor}
                            onChange={(e) => updateLocalPref('subtitleColor', e.target.value)}
                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                        >
                            <option value="white">White</option>
                            <option value="yellow">Yellow</option>
                            <option value="cyan">Cyan</option>
                            <option value="green">Green</option>
                        </select>
                    </div>

                    <div className="flex flex-col gap-2">
                        <label className="text-sm font-medium text-gray-400">Caption Background</label>
                        <select
                            value={localPrefs.subtitleBgOpacity}
                            onChange={(e) => updateLocalPref('subtitleBgOpacity', e.target.value)}
                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                        >
                            <option value="0">Transparent</option>
                            <option value="0.5">Semi-transparent</option>
                            <option value="0.75">Dark</option>
                            <option value="1">Solid</option>
                        </select>
                    </div>

                    <div className="flex flex-col gap-2">
                        <label className="text-sm font-medium text-gray-400">Caption Edge Style</label>
                        <select
                            value={localPrefs.subtitleEdgeStyle}
                            onChange={(e) => updateLocalPref('subtitleEdgeStyle', e.target.value)}
                            className="w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary [&>option]:bg-[#1a1a1a] [&>option]:text-white"
                        >
                            <option value="none">None</option>
                            <option value="outline">Outline</option>
                            <option value="shadow">Drop Shadow</option>
                        </select>
                    </div>

                    {/* Live preview of the caption styling */}
                    <div className="md:col-span-2 rounded-lg bg-black/60 px-4 py-6 flex items-center justify-center">
                        <span
                            style={{
                                color: SUBTITLE_COLORS[localPrefs.subtitleColor] ?? '#ffffff',
                                backgroundColor: `rgba(0,0,0,${localPrefs.subtitleBgOpacity})`,
                                fontSize: `${Number(localPrefs.subtitleFontSize) / 100}em`,
                                textShadow: localPrefs.subtitleEdgeStyle === 'outline'
                                    ? '-1px -1px 0 #000, 1px -1px 0 #000, -1px 1px 0 #000, 1px 1px 0 #000'
                                    : localPrefs.subtitleEdgeStyle === 'shadow'
                                        ? '2px 2px 3px rgba(0,0,0,0.9)'
                                        : 'none',
                                padding: '0.1em 0.35em',
                            }}
                        >
                            Caption preview — the quick brown fox
                        </span>
                    </div>

                </div>
            </div>
        </div>
    );
}
