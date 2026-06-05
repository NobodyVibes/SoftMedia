import { useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { QRCodeSVG } from 'qrcode.react';
import { ShieldCheck, Loader2, Copy, Check } from 'lucide-react';
import api from '../services/api';
import { Input } from '../components/ui/Input';
import { Button } from '../components/ui/Button';

type Step = 'form' | 'offer' | 'enroll' | 'recovery';

export default function SignupPage() {
    const navigate = useNavigate();

    const [firstName, setFirstName] = useState('');
    const [lastName, setLastName] = useState('');
    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [confirmPassword, setConfirmPassword] = useState('');
    const [inviteCode, setInviteCode] = useState('');
    const [error, setError] = useState('');
    const [isLoading, setIsLoading] = useState(false);

    // Optional 2FA setup (uses the signup access token transiently; we then send the
    // user to /login so the normal approval + 2FA-challenge flow applies).
    const [step, setStep] = useState<Step>('form');
    const [signupToken, setSignupToken] = useState('');
    const [secret, setSecret] = useState('');
    const [otpAuthUri, setOtpAuthUri] = useState('');
    const [code, setCode] = useState('');
    const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);
    const [twoFaError, setTwoFaError] = useState('');
    const [twoFaBusy, setTwoFaBusy] = useState(false);
    const [copied, setCopied] = useState(false);

    const authHeader = () => ({ headers: { Authorization: `Bearer ${signupToken}` } });

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError('');
        if (password !== confirmPassword) {
            setError("Passwords don't match");
            return;
        }
        setIsLoading(true);
        try {
            const res = await api.post<{ accessToken: string }>('/auth/signup', {
                username, password, inviteCode, firstName, lastName,
            });
            // Don't log in globally — keep the token only to (optionally) enroll 2FA now.
            setSignupToken(res.data.accessToken);
            setStep('offer');
        } catch (err: unknown) {
            const data = (err as { response?: { data?: unknown } })?.response?.data;
            setError(typeof data === 'string' ? data
                : (data as { message?: string })?.message || 'Failed to create account');
        } finally {
            setIsLoading(false);
        }
    };

    const startEnroll = async () => {
        setTwoFaBusy(true);
        setTwoFaError('');
        try {
            const res = await api.post<{ secret: string; otpAuthUri: string }>('/account/totp/enroll', {}, authHeader());
            setSecret(res.data.secret);
            setOtpAuthUri(res.data.otpAuthUri);
            setStep('enroll');
        } catch {
            setTwoFaError('Could not start 2FA setup. You can set it up later in My Account.');
        } finally {
            setTwoFaBusy(false);
        }
    };

    const confirmEnroll = async () => {
        setTwoFaBusy(true);
        setTwoFaError('');
        try {
            const res = await api.post<{ recoveryCodes: string[] }>('/account/totp/enroll/confirm', { code: code.trim() }, authHeader());
            setRecoveryCodes(res.data.recoveryCodes);
            setCode('');
            setStep('recovery');
        } catch {
            setTwoFaError('Invalid code. Check your authenticator app and try again.');
        } finally {
            setTwoFaBusy(false);
        }
    };

    const copyRecovery = async () => {
        try {
            await navigator.clipboard.writeText(recoveryCodes.join('\n'));
            setCopied(true);
            setTimeout(() => setCopied(false), 2000);
        } catch { /* clipboard may be blocked on insecure origins */ }
    };

    const finish = () => navigate('/login');

    const inputCls = 'w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary';
    const btnPrimary = 'inline-flex items-center justify-center gap-2 px-4 py-2 rounded-lg text-sm bg-primary hover:bg-primary/90 text-white disabled:opacity-50';

    return (
        <div className="min-h-screen flex items-center justify-center bg-background p-4">
            <div className="w-full max-w-md space-y-8 bg-surface p-8 rounded-xl shadow-2xl border border-slate-700">
                <div className="text-center">
                    <h2 className="text-3xl font-bold bg-clip-text text-transparent bg-brand-gradient">
                        {step === 'form' ? 'Create Account' : 'Secure your account'}
                    </h2>
                    <p className="mt-2 text-sm text-gray-400">
                        {step === 'form' ? 'Join SoftMedia to stream your collection' : 'Two-factor authentication is optional'}
                    </p>
                </div>

                {step === 'form' && (
                    <form className="mt-8 space-y-6" onSubmit={handleSubmit}>
                        <div className="space-y-4">
                            <div className="grid grid-cols-2 gap-4">
                                <Input id="firstName" type="text" label="First Name" placeholder="First Name"
                                    value={firstName} onChange={(e) => setFirstName(e.target.value)} required />
                                <Input id="lastName" type="text" label="Last Name" placeholder="Last Name"
                                    value={lastName} onChange={(e) => setLastName(e.target.value)} required />
                            </div>
                            <Input id="username" type="text" label="Username" placeholder="Choose a username"
                                value={username} onChange={(e) => setUsername(e.target.value)} required />
                            <Input id="password" type="password" label="Password" placeholder="Choose a password"
                                value={password} onChange={(e) => setPassword(e.target.value)} required />
                            <Input id="confirmPassword" type="password" label="Confirm Password" placeholder="Confirm your password"
                                value={confirmPassword} onChange={(e) => setConfirmPassword(e.target.value)} required />
                            <Input id="inviteCode" type="text" label="Invite Code (Optional)" placeholder="Enter invite code"
                                value={inviteCode} onChange={(e) => setInviteCode(e.target.value)} />
                        </div>

                        {error && (
                            <div className="text-red-500 text-sm text-center bg-red-500/10 p-2 rounded">{error}</div>
                        )}

                        <Button type="submit" className="w-full" isLoading={isLoading}>Sign up</Button>

                        <div className="text-center text-sm">
                            <span className="text-gray-400">Already have an account? </span>
                            <Link to="/login" className="text-primary hover:text-primary-dark font-medium">Sign in</Link>
                        </div>
                    </form>
                )}

                {step === 'offer' && (
                    <div className="space-y-5 text-center">
                        <ShieldCheck className="w-12 h-12 text-primary mx-auto" />
                        <p className="text-sm text-gray-300">
                            Your account is ready. Add a second factor with an authenticator app
                            (Google Authenticator, Authy, 1Password) for stronger security? You can always do this later in My Account.
                        </p>
                        {twoFaError && <p className="text-sm text-red-400">{twoFaError}</p>}
                        <div className="flex flex-col gap-2">
                            <button type="button" onClick={startEnroll} disabled={twoFaBusy} className={`${btnPrimary} w-full`}>
                                {twoFaBusy && <Loader2 size={16} className="animate-spin" />} Set up 2FA
                            </button>
                            <button type="button" onClick={finish}
                                className="w-full px-4 py-2 text-sm bg-white/5 hover:bg-white/10 text-white rounded-lg">
                                Skip for now
                            </button>
                        </div>
                    </div>
                )}

                {step === 'enroll' && (
                    <div className="space-y-4">
                        <p className="text-sm text-gray-400">Scan this QR code with your authenticator app, then enter the 6-digit code.</p>
                        <div className="bg-white p-3 rounded-lg inline-block mx-auto">
                            <QRCodeSVG value={otpAuthUri} size={180} />
                        </div>
                        <p className="text-xs text-gray-500 break-all">Can't scan? Enter this key manually: <code className="text-gray-300">{secret}</code></p>
                        <input type="text" inputMode="numeric" placeholder="6-digit code" value={code}
                            onChange={(e) => setCode(e.target.value)} className={inputCls} />
                        {twoFaError && <p className="text-sm text-red-400">{twoFaError}</p>}
                        <div className="flex gap-2">
                            <button type="button" onClick={confirmEnroll} disabled={twoFaBusy} className={btnPrimary}>
                                {twoFaBusy && <Loader2 size={16} className="animate-spin" />} Confirm
                            </button>
                            <button type="button" onClick={finish}
                                className="px-4 py-2 text-sm bg-white/5 hover:bg-white/10 text-white rounded-lg">Skip</button>
                        </div>
                    </div>
                )}

                {step === 'recovery' && (
                    <div className="space-y-3">
                        <p className="text-sm text-green-400 font-medium">2FA enabled. Save your recovery codes now — they're shown only once.</p>
                        <p className="text-xs text-gray-400">Each code works once if you lose access to your authenticator.</p>
                        <div className="grid grid-cols-2 gap-2 bg-black/30 rounded-lg p-3">
                            {recoveryCodes.map((rc) => (<code key={rc} className="text-sm font-mono text-white">{rc}</code>))}
                        </div>
                        <div className="flex gap-2">
                            <button type="button" onClick={copyRecovery}
                                className="inline-flex items-center gap-2 px-4 py-2 text-sm bg-white/10 hover:bg-white/20 text-white rounded-lg">
                                {copied ? <Check size={16} className="text-green-400" /> : <Copy size={16} />} Copy codes
                            </button>
                            <button type="button" onClick={finish} className={btnPrimary}>Done — go to sign in</button>
                        </div>
                    </div>
                )}
            </div>
        </div>
    );
}
