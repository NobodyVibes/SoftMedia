import { X, Info } from 'lucide-react';
import { useTranslation } from 'react-i18next';

export interface StreamReasonCode {
    code: string;
    params: Record<string, string>;
}

export interface ExplanationPlan {
    method: 'DirectPlay' | 'Remux' | 'Transcode';
    videoCodec: string;
    audioCodec: string;
    resolution: string;
    isHdr: boolean;
    reasonCodes?: StreamReasonCode[];
    reason: string;
}

/**
 * "Why is this playing this way?" — translates the server's structured reason codes
 * (P2-WI-002) into plain-language sentences. Falls back to the free-form `reason`
 * string if the server sent no codes (older server / unmapped code).
 */
export function TranscodeExplanationModal({ plan, onClose }: { plan: ExplanationPlan; onClose: () => void }) {
    const { t } = useTranslation();

    const methodTitle =
        plan.method === 'DirectPlay' ? t('explain.method.directplay')
            : plan.method === 'Remux' ? t('explain.method.remux')
                : t('explain.method.transcode');

    const codes = plan.reasonCodes ?? [];

    return (
        <div
            className="fixed inset-0 bg-black/80 backdrop-blur-sm flex items-center justify-center z-[60] p-4"
            role="dialog"
            aria-modal="true"
        >
            <div className="bg-[#1a1a1a] rounded-xl p-6 max-w-md w-full border border-white/10 shadow-2xl">
                <div className="flex items-center justify-between mb-4">
                    <div className="flex items-center gap-2">
                        <Info className="w-5 h-5 text-primary" />
                        <h3 className="text-lg font-semibold text-white">{t('explain.title')}</h3>
                    </div>
                    <button
                        type="button"
                        onClick={onClose}
                        className="p-1.5 rounded hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-gray-400"
                        aria-label={t('Close')}
                    >
                        <X size={18} />
                    </button>
                </div>

                <p className="text-sm font-medium text-white mb-3">{methodTitle}</p>

                {plan.method === 'DirectPlay' && codes.length <= 1 ? (
                    <p className="text-sm text-gray-300">{t('explain.directplay.detail')}</p>
                ) : codes.length > 0 ? (
                    <ul className="space-y-2">
                        {codes.map((rc, i) => (
                            <li key={i} className="text-sm text-gray-300 flex gap-2">
                                <span className="text-primary mt-0.5">•</span>
                                <span>{t(`explain.reason.${rc.code}`, { ...rc.params, defaultValue: plan.reason })}</span>
                            </li>
                        ))}
                    </ul>
                ) : (
                    <p className="text-sm text-gray-300">{plan.reason || t('explain.transcode.generic')}</p>
                )}
            </div>
        </div>
    );
}
