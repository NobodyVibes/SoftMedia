import { useTranslation } from 'react-i18next';
import { Modal } from '../ui/Modal';
import type { MediaVersion } from '../../types';

interface GuardrailReasonCode {
    code: string;
    params: Record<string, string>;
}

/** The QS-WI-005 facts the guardrail consumes from the server's stream plan. */
export interface HdrGuardrailPlan {
    toneMapIsSoftware: boolean;
    hardwareAccelerationEnabled: boolean;
    reasonCodes?: GuardrailReasonCode[];
}

/** Codes that name why the tone-map (or the transcode behind it) is happening. */
const CAUSE_CODE_PATTERN = /^(hdr\.tonemap|subtitle\.burn-in)/;
/** Codes that describe the playback method itself, never a cause worth prompting about. */
const NON_CAUSE_CODES = new Set(['directplay.supported', 'remux.container']);

/**
 * QS-WI-005 — the pre-play HDR guardrail prompt. A NEW surface, distinct from the
 * user-invoked TranscodeExplanationModal: it fires BEFORE playback whenever the computed
 * plan would tone-map (HDR → SDR), composed of lines that are each true-to-cause:
 *
 *  - the quality line, always (converted HDR never looks as good as native SDR);
 *  - the resource line, only when THIS plan's tone-map runs in software — the flag comes
 *    from the server's pipeline authority (TranscodeProfileBuilder.SelectToneMapPipeline),
 *    never a hardcoded vendor list, with wording split by whether hw accel is configured;
 *  - the cause line(s), consuming the SAME `explain.reason.*` i18n strings as the
 *    explainer (QS-WI-004) — no parallel wording.
 *
 * The version button OFFERS the best SDR sibling from the version group; it never
 * auto-picks one (standing owner decision). In block mode (admin BlockHdrTranscode)
 * "Play anyway" and "Never show again" are absent — only the SDR version (when one
 * exists) or cancel.
 */
export function HdrTranscodePrompt({
    plan,
    versionOffer,
    mode,
    onPlayAnyway,
    onPlayVersion,
    onNeverShowAgain,
    onCancel,
}: {
    plan: HdrGuardrailPlan;
    versionOffer: MediaVersion | null;
    mode: 'warn' | 'block';
    onPlayAnyway: () => void;
    onPlayVersion: (version: MediaVersion) => void;
    onNeverShowAgain: () => void;
    onCancel: () => void;
}) {
    const { t } = useTranslation();

    const codes = plan.reasonCodes ?? [];
    let causes = codes.filter(rc => CAUSE_CODE_PATTERN.test(rc.code));
    if (causes.length === 0) {
        // No HDR-specific cause on the plan (older server): fall back to the first
        // meaningful transcode cause so the prompt still says WHY.
        causes = codes.filter(rc => !NON_CAUSE_CODES.has(rc.code)).slice(0, 1);
    }

    return (
        <Modal isOpen onClose={onCancel} title={t('hdrguard.title')}>
            <div className="space-y-4">
                <p className="text-sm text-gray-300">{t('hdrguard.quality')}</p>

                {plan.toneMapIsSoftware && (
                    <p className="text-sm text-amber-300/90">
                        {plan.hardwareAccelerationEnabled
                            ? t('hdrguard.load.partial')
                            : t('hdrguard.load.noHwAccel')}
                    </p>
                )}

                {causes.length > 0 && (
                    <ul className="text-xs text-gray-400 list-disc pl-5 space-y-1">
                        {causes.map(rc => (
                            <li key={rc.code}>
                                {t(`explain.reason.${rc.code}`, { ...rc.params, defaultValue: '' }) || rc.code}
                            </li>
                        ))}
                    </ul>
                )}

                {mode === 'block' && (
                    <p className="text-sm text-red-300/90">{t('hdrguard.blocked')}</p>
                )}

                <div className="flex flex-col gap-2 pt-2">
                    {mode === 'warn' && (
                        <button
                            type="button"
                            onClick={onPlayAnyway}
                            className="px-4 py-2 rounded bg-primary hover:bg-primary/90 text-white transition-colors"
                        >
                            {t('hdrguard.playAnyway')}
                        </button>
                    )}
                    {versionOffer && (
                        <button
                            type="button"
                            onClick={() => onPlayVersion(versionOffer)}
                            className={`px-4 py-2 rounded transition-colors ${mode === 'block'
                                ? 'bg-primary hover:bg-primary/90 text-white'
                                : 'bg-gray-700 hover:bg-gray-600 text-gray-100'}`}
                        >
                            {t('hdrguard.playVersion', { label: versionOffer.label })}
                        </button>
                    )}
                    <button
                        type="button"
                        onClick={onCancel}
                        className="px-4 py-2 rounded text-gray-300 hover:bg-gray-700 transition-colors"
                    >
                        {t('hdrguard.cancel')}
                    </button>
                    {mode === 'warn' && (
                        <button
                            type="button"
                            onClick={onNeverShowAgain}
                            className="text-xs text-gray-500 hover:text-gray-300 transition-colors self-center pt-1"
                        >
                            {t('hdrguard.neverShow')}
                        </button>
                    )}
                </div>
            </div>
        </Modal>
    );
}
