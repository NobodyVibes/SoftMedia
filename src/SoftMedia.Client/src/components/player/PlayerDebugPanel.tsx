import { useEffect, useState } from 'react';
import { getAccessToken } from '../../store/authStore';

interface DebugInfo {
    playbackMode: string;
    isTranscoding: boolean;
    message?: string;
    clientCapabilities?: {
        videoCodecs?: string[];
        audioCodecs?: string[];
        supportsHdr?: boolean;
        maxAudioChannels?: number;
        maxResolution?: number;
        maxBitrate?: number;
        requestedQuality?: string;
        supportedContainers?: string[];
        supportedSubtitleFormats?: string[];
        displaySupportsHdr?: boolean;
        codecSupportsHdr?: boolean;
    };
    serverSettings?: {
        outputVideoCodec?: string;
        maxResolution?: string;
        preserveHdr?: boolean;
        enableAv1?: boolean;
        hardwareAcceleration?: string;
        preset?: string;
        crf?: string;
        targetAudioChannels?: string;
    };
    sourceMedia?: {
        videoCodec?: string;
        audioCodec?: string;
        resolution?: string;
        container?: string;
        duration?: number;
    };
    decision?: {
        targetCodec: string;
        targetResolution: string;
        preserveHdr: boolean;
        toneMapped: boolean;
        subtitleBurnIn: boolean;
        subtitleTrack: number | null;
        subtitleLanguage?: string;
    };
    probe?: {
        filePath?: string;
        videoCodec?: string;
        pixelFormat?: string;
        colorSpace?: string;
        colorTransfer?: string;
        colorPrimaries?: string;
        resolution?: string;
        hasHdrMetadata?: boolean;
        isHdr?: boolean;
        audioCodec?: string;
        audioChannels?: number;
        error?: string;
    };
    selectedSubtitleTrack?: number | null;
    sessionDirectory?: string;
    probedAt?: string;
}

interface PlayerDebugPanelProps {
    mediaId: string;
    token: string;
    subtitleTrack: number | null;
    clientCapabilities?: any;
    onClose: () => void;
    streamId?: string;
}

/**
 * Debug panel overlay showing playback decision pipeline.
 */
export function PlayerDebugPanel({ mediaId, token, subtitleTrack, clientCapabilities, onClose, streamId }: PlayerDebugPanelProps) {
    const [debugInfo, setDebugInfo] = useState<DebugInfo | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        const fetchDebugInfo = async () => {
            try {
                setLoading(true);
                const subParam = subtitleTrack !== null ? `&sub=${subtitleTrack}` : '';
                const sidParam = streamId ? `&sid=${streamId}` : '';

                // POST request with client capabilities. WS-6: POSTs authenticate via
                // the Authorization header (query tokens are media/cast + GET-only now).
                const query = [subParam, sidParam].join('').replace(/^&/, '?');
                const response = await fetch(`/api/v1/transcode/${mediaId}/debug${query}`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${getAccessToken()}` },
                    body: JSON.stringify(clientCapabilities || {
                        videoCodecs: ['h264'],
                        audioCodecs: ['aac'],
                        supportsHdr: false,
                        maxAudioChannels: 2
                    })
                });

                if (!response.ok) {
                    throw new Error(`HTTP ${response.status}: ${response.statusText}`);
                }

                const data = await response.json();
                setDebugInfo(data);
                setError(null);
            } catch (err) {
                setError(err instanceof Error ? err.message : 'Failed to fetch debug info');
            } finally {
                setLoading(false);
            }
        };

        fetchDebugInfo();
    }, [mediaId, token, subtitleTrack, clientCapabilities]);

    // Close on Escape key
    useEffect(() => {
        const handleKeyDown = (e: KeyboardEvent) => {
            if (e.key === 'Escape' || e.key.toLowerCase() === 'd') {
                onClose();
            }
        };
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [onClose]);

    const [copied, setCopied] = useState(false);

    const handleExport = async () => {
        if (!debugInfo) return;
        try {
            await navigator.clipboard.writeText(JSON.stringify(debugInfo, null, 2));
            setCopied(true);
            setTimeout(() => setCopied(false), 2000);
        } catch (err) {
            console.error('Failed to copy debug info:', err);
        }
    };

    const renderValue = (value: unknown): string => {
        if (value === null || value === undefined) return '—';
        if (typeof value === 'boolean') return value ? '✓ Yes' : '✗ No';
        if (Array.isArray(value)) return value.join(', ') || '—';
        return String(value);
    };

    return (
        <div
            role="button"
            aria-label="Close debug panel"
            tabIndex={-1}
            className="absolute inset-0 bg-black/85 z-50 flex items-center justify-center p-4"
            onClick={(e) => e.target === e.currentTarget && onClose()}
            onKeyDown={(e) => {
                if (e.key === 'Escape') onClose();
            }}
        >
            <div className="bg-gradient-to-br from-gray-900 to-gray-800 rounded-xl border border-gray-700 max-w-4xl w-full max-h-[85vh] overflow-auto shadow-2xl">
                {/* Header */}
                <div className="flex items-center justify-between px-6 py-4 border-b border-gray-700 sticky top-0 bg-gray-900/95 backdrop-blur z-10">
                    <h2 className="text-xl font-semibold text-white flex items-center gap-2">
                        <span className="text-2xl">🔍</span> Playback Debug Pipeline
                    </h2>
                    <div className="flex items-center gap-3">
                        <button
                            type="button"
                            onClick={handleExport}
                            aria-label={copied ? 'Debug info copied to clipboard' : 'Copy debug info to clipboard'}
                            className={`px-3 py-1.5 rounded-lg text-sm font-medium transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 ${copied
                                ? 'bg-green-500/20 text-green-400 border border-green-500/50'
                                : 'bg-indigo-500/20 text-indigo-300 border border-indigo-500/30 hover:bg-indigo-500/30'
                                }`}
                        >
                            {copied ? '✓ Copied!' : '📤 Export'}
                        </button>
                        <button
                            type="button"
                            onClick={onClose}
                            aria-label="Close debug panel"
                            className="text-gray-400 hover:text-white transition-colors p-2 min-w-[44px] min-h-[44px] flex items-center justify-center hover:bg-gray-700 focus-visible:bg-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded-lg"
                        >
                            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                            </svg>
                        </button>
                    </div>
                </div>

                {/* Content */}
                <div className="p-6 space-y-6">
                    {loading && (
                        <div className="flex items-center justify-center py-8">
                            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-500"></div>
                            <span className="ml-3 text-gray-300">Probing transcoded output...</span>
                        </div>
                    )}

                    {error && (
                        <div className="bg-red-900/30 border border-red-700 rounded-lg p-4 text-red-300">
                            {error}
                        </div>
                    )}

                    {debugInfo && !loading && (
                        <>
                            {/* Playback Mode Badge */}
                            <div className="flex items-center gap-3 mb-4">
                                <span className={`px-3 py-1 rounded-full text-sm font-medium ${debugInfo.isTranscoding
                                    ? 'bg-amber-600/20 text-amber-400 border border-amber-600/30'
                                    : 'bg-green-600/20 text-green-400 border border-green-600/30'
                                    }`}>
                                    {debugInfo.playbackMode}
                                </span>
                                {debugInfo.message && (
                                    <span className="text-gray-400 text-sm">{debugInfo.message}</span>
                                )}
                            </div>

                            {/* Pipeline Grid */}
                            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">

                                {debugInfo.clientCapabilities && (
                                    <Section title="1. Client Sent" color="cyan">
                                        <Row label="Video Codecs" value={renderValue(debugInfo.clientCapabilities.videoCodecs)} />
                                        <Row label="Audio Codecs" value={renderValue(debugInfo.clientCapabilities.audioCodecs)} />
                                        <Row label="Max Audio Ch" value={debugInfo.clientCapabilities.maxAudioChannels} />
                                        <Row label="Supports HDR" value={renderValue(debugInfo.clientCapabilities.supportsHdr)} highlight={debugInfo.clientCapabilities.supportsHdr} />
                                        <div className="pl-4 border-l border-cyan-500/20 my-1">
                                            <Row label="↳ Display" value={renderValue(debugInfo.clientCapabilities.displaySupportsHdr)} />
                                            <Row label="↳ Codec" value={renderValue(debugInfo.clientCapabilities.codecSupportsHdr)} />
                                        </div>
                                        <Row label="Subtitle Formats" value={renderValue(debugInfo.clientCapabilities.supportedSubtitleFormats)} />
                                        <Row label="Quality Req" value={debugInfo.clientCapabilities.requestedQuality ?? 'auto'} />
                                    </Section>
                                )}

                                {/* 2. Server Settings */}
                                {debugInfo.serverSettings && (
                                    <Section title="2. Server Settings" color="blue">
                                        <Row label="Output Codec" value={debugInfo.serverSettings.outputVideoCodec} />
                                        <Row label="Max Resolution" value={debugInfo.serverSettings.maxResolution} />
                                        <Row label="Preserve HDR" value={renderValue(debugInfo.serverSettings.preserveHdr)} />
                                        <Row label="Enable AV1" value={renderValue(debugInfo.serverSettings.enableAv1)} />
                                        <Row label="HW Accel" value={debugInfo.serverSettings.hardwareAcceleration} />
                                        <Row label="Audio Channels" value={debugInfo.serverSettings.targetAudioChannels} />
                                    </Section>
                                )}

                                {/* 3. Source Media */}
                                {debugInfo.sourceMedia && (
                                    <Section title="3. Source File" color="indigo">
                                        <Row label="Video Codec" value={debugInfo.sourceMedia.videoCodec} />
                                        <Row label="Audio Codec" value={debugInfo.sourceMedia.audioCodec} />
                                        <Row label="Resolution" value={debugInfo.sourceMedia.resolution} />
                                        <Row label="Container" value={debugInfo.sourceMedia.container} />
                                    </Section>
                                )}

                                {/* 4. Decision */}
                                {debugInfo.decision && (
                                    <Section title="4. Backend Decision" color="purple">
                                        <Row label="Target Codec" value={debugInfo.decision.targetCodec} />
                                        <Row label="Target Res" value={debugInfo.decision.targetResolution} />
                                        <Row label="Preserve HDR" value={renderValue(debugInfo.decision.preserveHdr)} highlight={debugInfo.decision.preserveHdr} />
                                        <Row label="Tone Mapped" value={renderValue(debugInfo.decision.toneMapped)} />
                                        <Row label="Subtitle Track" value={debugInfo.decision.subtitleTrack ?? 'None'} />
                                        <Row label="Subtitle Lang" value={debugInfo.decision.subtitleLanguage ?? '—'} />
                                        <Row label="Subtitle Burn" value={renderValue(debugInfo.decision.subtitleBurnIn)} highlight={debugInfo.decision.subtitleBurnIn} />
                                    </Section>
                                )}

                                {/* 5. Actual Output */}
                                {debugInfo.probe && (
                                    <Section title="5. Actual Output (FFprobe)" color="green">
                                        {debugInfo.probe.error ? (
                                            <div className="text-amber-400 text-sm">{debugInfo.probe.error}</div>
                                        ) : (
                                            <>
                                                <Row label="Video Codec" value={debugInfo.probe.videoCodec} />
                                                <Row label="Resolution" value={debugInfo.probe.resolution} />
                                                <Row label="Pixel Format" value={debugInfo.probe.pixelFormat} />
                                                <Row label="Color Space" value={debugInfo.probe.colorSpace} />
                                                <Row label="Color Transfer" value={debugInfo.probe.colorTransfer} highlight={debugInfo.probe.colorTransfer === 'smpte2084'} />
                                                <Row label="HDR Metadata" value={renderValue(debugInfo.probe.hasHdrMetadata)} highlight={debugInfo.probe.hasHdrMetadata} />
                                                {debugInfo.probe.audioCodec && <Row label="Audio Codec" value={debugInfo.probe.audioCodec} />}
                                                {debugInfo.probe.audioChannels && <Row label="Audio Ch" value={debugInfo.probe.audioChannels} />}
                                            </>
                                        )}
                                    </Section>
                                )}
                            </div>

                            {/* File Path */}
                            {debugInfo.probe?.filePath && (
                                <div className="mt-4 pt-4 border-t border-gray-700">
                                    <span className="text-xs text-gray-500 font-mono block break-all">
                                        Probed: {debugInfo.probe.filePath}
                                    </span>
                                </div>
                            )}
                        </>
                    )}
                </div>

                {/* Footer */}
                <div className="px-6 py-3 border-t border-gray-700 text-center text-xs text-gray-500">
                    Press <kbd className="px-1.5 py-0.5 bg-gray-700 rounded">D</kbd> or <kbd className="px-1.5 py-0.5 bg-gray-700 rounded">Esc</kbd> to close
                </div>
            </div>
        </div>
    );
}

function Section({ title, color, children }: { title: string; color: string; children: React.ReactNode }) {
    const colors: Record<string, string> = {
        cyan: 'text-cyan-400 border-cyan-600/30',
        blue: 'text-blue-400 border-blue-600/30',
        indigo: 'text-indigo-400 border-indigo-600/30',
        purple: 'text-purple-400 border-purple-600/30',
        green: 'text-green-400 border-green-600/30',
    };
    return (
        <div className={`bg-gray-800/50 rounded-lg p-4 border ${colors[color] || 'border-gray-700'}`}>
            <h4 className={`text-xs font-semibold uppercase tracking-wider mb-3 ${colors[color]?.split(' ')[0] || 'text-gray-400'}`}>
                {title}
            </h4>
            <div className="space-y-1.5 text-sm">
                {children}
            </div>
        </div>
    );
}

function Row({ label, value, highlight }: { label: string; value: unknown; highlight?: boolean }) {
    const valueStr = value === null || value === undefined ? '—' : String(value);
    return (
        <div className="flex justify-between items-center gap-2">
            <span className="text-gray-400 text-xs">{label}</span>
            <span className={`font-mono text-xs ${highlight ? 'text-green-400' : 'text-white'}`}>
                {valueStr}
            </span>
        </div>
    );
}

export default PlayerDebugPanel;
