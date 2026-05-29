import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { QRCodeSVG } from 'qrcode.react';
import { ShieldCheck, ShieldOff, Loader2, Copy, Check } from 'lucide-react';
import { accountService } from '../../services/accountService';

type Step = 'idle' | 'enrolling' | 'recovery';

export function TotpCard() {
    const queryClient = useQueryClient();
    const { data: status, isLoading } = useQuery({
        queryKey: ['totpStatus'],
        queryFn: accountService.getTotpStatus,
    });

    const [step, setStep] = useState<Step>('idle');
    const [secret, setSecret] = useState('');
    const [otpAuthUri, setOtpAuthUri] = useState('');
    const [code, setCode] = useState('');
    const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);
    const [error, setError] = useState('');
    const [copied, setCopied] = useState(false);

    // Disable flow
    const [disablePassword, setDisablePassword] = useState('');
    const [disableCode, setDisableCode] = useState('');
    const [showDisable, setShowDisable] = useState(false);

    const refresh = () => queryClient.invalidateQueries({ queryKey: ['totpStatus'] });

    const enrollMutation = useMutation({
        mutationFn: accountService.enrollTotp,
        onSuccess: (res) => {
            setSecret(res.secret);
            setOtpAuthUri(res.otpAuthUri);
            setStep('enrolling');
            setError('');
        },
        onError: () => setError('Could not start enrollment.'),
    });

    const confirmMutation = useMutation({
        mutationFn: () => accountService.confirmTotp(code.trim()),
        onSuccess: (res) => {
            setRecoveryCodes(res.recoveryCodes);
            setStep('recovery');
            setCode('');
            setError('');
            refresh();
        },
        onError: () => setError('Invalid code. Check your authenticator app and try again.'),
    });

    const disableMutation = useMutation({
        mutationFn: () => accountService.disableTotp(disablePassword, disableCode.trim()),
        onSuccess: () => {
            setShowDisable(false);
            setDisablePassword('');
            setDisableCode('');
            setError('');
            refresh();
        },
        onError: () => setError('Could not disable 2FA. Check your password and code.'),
    });

    const copyRecovery = async () => {
        await navigator.clipboard.writeText(recoveryCodes.join('\n'));
        setCopied(true);
        setTimeout(() => setCopied(false), 2000);
    };

    const inputCls = 'w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary';
    const btnPrimary = 'inline-flex items-center gap-2 px-4 py-2 rounded-lg text-sm bg-primary hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white disabled:opacity-50';

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center gap-3 mb-4">
                {status?.enabled ? <ShieldCheck className="w-5 h-5 text-green-400" /> : <ShieldOff className="w-5 h-5 text-primary" />}
                <h2 className="text-lg font-semibold">Two-Factor Authentication</h2>
            </div>

            {isLoading ? (
                <Loader2 className="w-5 h-5 animate-spin text-primary" />
            ) : status?.enabled && step !== 'recovery' ? (
                <>
                    <p className="text-sm text-gray-400 mb-4">
                        2FA is <span className="text-green-400 font-medium">enabled</span>. You'll be asked for a code when you sign in.
                    </p>
                    {!showDisable ? (
                        <button type="button" onClick={() => { setShowDisable(true); setError(''); }}
                            className="px-4 py-2 text-sm bg-red-500/20 hover:bg-red-500/30 text-red-400 rounded-lg border border-red-500/10">
                            Disable 2FA
                        </button>
                    ) : (
                        <div className="space-y-3 max-w-sm">
                            <input type="password" placeholder="Account password" value={disablePassword}
                                onChange={(e) => setDisablePassword(e.target.value)} className={inputCls} />
                            <input type="text" inputMode="numeric" placeholder="Authenticator or recovery code"
                                value={disableCode} onChange={(e) => setDisableCode(e.target.value)} className={inputCls} />
                            {error && <p className="text-sm text-red-400">{error}</p>}
                            <div className="flex gap-2">
                                <button type="button" onClick={() => disableMutation.mutate()} disabled={disableMutation.isPending}
                                    className="px-4 py-2 text-sm bg-red-500 hover:bg-red-600 text-white rounded-lg disabled:opacity-50">
                                    Confirm Disable
                                </button>
                                <button type="button" onClick={() => { setShowDisable(false); setError(''); }}
                                    className="px-4 py-2 text-sm bg-white/5 hover:bg-white/10 text-white rounded-lg">Cancel</button>
                            </div>
                        </div>
                    )}
                </>
            ) : step === 'idle' ? (
                <>
                    <p className="text-sm text-gray-400 mb-4">
                        Add a second factor with an authenticator app (Google Authenticator, Authy, etc.) for stronger account security.
                    </p>
                    <button type="button" onClick={() => enrollMutation.mutate()} disabled={enrollMutation.isPending} className={btnPrimary}>
                        {enrollMutation.isPending && <Loader2 size={16} className="animate-spin" />}
                        Enable 2FA
                    </button>
                </>
            ) : step === 'enrolling' ? (
                <div className="space-y-4 max-w-sm">
                    <p className="text-sm text-gray-400">Scan this QR code with your authenticator app, then enter the 6-digit code to confirm.</p>
                    <div className="bg-white p-3 rounded-lg inline-block">
                        <QRCodeSVG value={otpAuthUri} size={180} />
                    </div>
                    <p className="text-xs text-gray-500">
                        Can't scan? Enter this key manually: <code className="text-gray-300 break-all">{secret}</code>
                    </p>
                    <input type="text" inputMode="numeric" placeholder="6-digit code" value={code}
                        onChange={(e) => setCode(e.target.value)} className={inputCls} />
                    {error && <p className="text-sm text-red-400">{error}</p>}
                    <div className="flex gap-2">
                        <button type="button" onClick={() => confirmMutation.mutate()} disabled={confirmMutation.isPending} className={btnPrimary}>
                            {confirmMutation.isPending && <Loader2 size={16} className="animate-spin" />}
                            Confirm
                        </button>
                        <button type="button" onClick={() => { setStep('idle'); setError(''); setCode(''); }}
                            className="px-4 py-2 text-sm bg-white/5 hover:bg-white/10 text-white rounded-lg">Cancel</button>
                    </div>
                </div>
            ) : (
                /* recovery */
                <div className="space-y-3 max-w-sm">
                    <p className="text-sm text-green-400 font-medium">2FA enabled. Save your recovery codes now — they're shown only once.</p>
                    <p className="text-xs text-gray-400">Each code works once if you lose access to your authenticator.</p>
                    <div className="grid grid-cols-2 gap-2 bg-black/30 rounded-lg p-3">
                        {recoveryCodes.map((rc) => (
                            <code key={rc} className="text-sm font-mono text-white">{rc}</code>
                        ))}
                    </div>
                    <div className="flex gap-2">
                        <button type="button" onClick={copyRecovery}
                            className="inline-flex items-center gap-2 px-4 py-2 text-sm bg-white/10 hover:bg-white/20 text-white rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400">
                            {copied ? <Check size={16} className="text-green-400" /> : <Copy size={16} />}
                            Copy codes
                        </button>
                        <button type="button" onClick={() => setStep('idle')} className={btnPrimary}>Done</button>
                    </div>
                </div>
            )}
        </div>
    );
}
