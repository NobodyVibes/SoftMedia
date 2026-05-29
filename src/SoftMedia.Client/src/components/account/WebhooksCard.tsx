import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Webhook, Plus, Trash2, Send, Copy, Check, Loader2, X } from 'lucide-react';
import { toast } from 'sonner';
import { accountService, type WebhookDto } from '../../services/accountService';

const AVAILABLE_EVENTS = [
    { value: 'library.scan.completed', label: 'Library scan completed' },
    { value: 'library.scan.failed', label: 'Library scan failed' },
    { value: 'webhook.test', label: 'Test events' },
];

export function WebhooksCard() {
    const queryClient = useQueryClient();
    const [showForm, setShowForm] = useState(false);
    const [url, setUrl] = useState('');
    const [events, setEvents] = useState<string[]>(['library.scan.completed']);
    const [mintedSecret, setMintedSecret] = useState<string | null>(null);
    const [copied, setCopied] = useState(false);
    const [error, setError] = useState('');

    const { data: hooks = [], isLoading } = useQuery<WebhookDto[]>({
        queryKey: ['webhooks'],
        queryFn: accountService.listWebhooks,
    });
    const invalidate = () => queryClient.invalidateQueries({ queryKey: ['webhooks'] });

    const createMutation = useMutation({
        mutationFn: () => accountService.createWebhook(url, events),
        onSuccess: (res) => {
            setMintedSecret(res.secret);
            setShowForm(false);
            setUrl('');
            setEvents(['library.scan.completed']);
            setError('');
            invalidate();
        },
        onError: (e: unknown) => setError(e instanceof Error ? e.message : 'Failed to create webhook'),
    });

    const deleteMutation = useMutation({
        mutationFn: accountService.deleteWebhook,
        onSuccess: invalidate,
    });

    const testMutation = useMutation({
        mutationFn: accountService.testWebhook,
        onSuccess: () => toast.success('Test event enqueued'),
        onError: () => toast.error('Failed to send test event'),
    });

    const toggleEvent = (e: string) =>
        setEvents((prev) => (prev.includes(e) ? prev.filter((x) => x !== e) : [...prev, e]));

    const copySecret = async () => {
        if (!mintedSecret) return;
        await navigator.clipboard.writeText(mintedSecret);
        setCopied(true);
        setTimeout(() => setCopied(false), 2000);
    };

    const inputCls = 'w-full bg-[#1a1a1a] border border-white/10 rounded-lg px-4 py-2.5 text-white focus:outline-none focus:border-primary';
    const btnPrimary = 'inline-flex items-center gap-2 px-4 py-2 text-sm bg-primary hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg disabled:opacity-50';

    return (
        <div className="bg-white/5 rounded-xl p-6 border border-white/10">
            <div className="flex items-center justify-between mb-4">
                <div className="flex items-center gap-3">
                    <Webhook className="w-5 h-5 text-primary" />
                    <h2 className="text-lg font-semibold">Webhooks</h2>
                </div>
                {!showForm && (
                    <button type="button" onClick={() => setShowForm(true)}
                        className="inline-flex items-center gap-2 px-3 py-1.5 text-sm bg-primary hover:bg-primary/90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 text-white rounded-lg">
                        <Plus size={16} /> New Webhook
                    </button>
                )}
            </div>

            <p className="text-sm text-gray-400 mb-4">
                POST signed JSON to your own endpoint (Discord, ntfy, Home Assistant, etc.) on selected events.
            </p>

            {mintedSecret && (
                <div className="mb-4 p-4 bg-blue-500/10 border border-blue-500/30 rounded-lg">
                    <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0 flex-1">
                            <p className="text-sm text-blue-300 font-medium mb-1">
                                Signing secret — copy it now, it won't be shown again. Verify the
                                <code className="text-white"> X-SoftMedia-Signature </code> header with it.
                            </p>
                            <code className="block text-xs font-mono text-white break-all bg-black/30 rounded px-2 py-1.5">{mintedSecret}</code>
                        </div>
                        <div className="flex items-center gap-1 shrink-0">
                            <button type="button" onClick={copySecret} className="p-2 rounded hover:bg-white/10 text-blue-300 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400">
                                {copied ? <Check size={16} className="text-green-400" /> : <Copy size={16} />}
                            </button>
                            <button type="button" onClick={() => setMintedSecret(null)} className="p-2 rounded hover:bg-white/10 text-gray-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400">
                                <X size={16} />
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {showForm && (
                <div className="mb-6 p-4 bg-black/20 border border-white/10 rounded-lg space-y-4">
                    <div>
                        <label className="block text-sm text-gray-400 mb-2">Endpoint URL</label>
                        <input type="url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://example.com/webhook" className={inputCls} />
                    </div>
                    <div>
                        <span className="block text-sm text-gray-400 mb-2">Events</span>
                        <div className="space-y-2">
                            {AVAILABLE_EVENTS.map((e) => (
                                <label key={e.value} className="flex items-center gap-3 cursor-pointer">
                                    <input type="checkbox" checked={events.includes(e.value)} onChange={() => toggleEvent(e.value)} className="accent-primary" />
                                    <span className="text-sm text-white">{e.label}</span>
                                </label>
                            ))}
                        </div>
                    </div>
                    {error && <p className="text-sm text-red-400">{error}</p>}
                    <div className="flex gap-3">
                        <button type="button" onClick={() => createMutation.mutate()} disabled={createMutation.isPending || !url || events.length === 0} className={btnPrimary}>
                            {createMutation.isPending && <Loader2 size={16} className="animate-spin" />}
                            Create
                        </button>
                        <button type="button" onClick={() => { setShowForm(false); setError(''); }} className="px-4 py-2 text-sm bg-white/5 hover:bg-white/10 text-white rounded-lg">Cancel</button>
                    </div>
                </div>
            )}

            {isLoading ? (
                <Loader2 className="w-5 h-5 animate-spin text-primary" />
            ) : hooks.length === 0 ? (
                <p className="text-sm text-gray-500 py-2">No webhooks configured.</p>
            ) : (
                <ul className="divide-y divide-white/5">
                    {hooks.map((h) => (
                        <li key={h.id} className="py-3 flex items-center justify-between gap-3">
                            <div className="min-w-0">
                                <p className="text-white truncate">{h.url}</p>
                                <p className="text-xs text-gray-500">
                                    {h.events.join(', ')}
                                    {h.lastDeliveryStatus && ` · last: ${h.lastDeliveryStatus}`}
                                </p>
                            </div>
                            <div className="flex items-center gap-1 shrink-0">
                                <button type="button" onClick={() => testMutation.mutate(h.id)} disabled={testMutation.isPending}
                                    className="p-2 rounded hover:bg-primary/20 text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400" title="Send test event">
                                    <Send size={16} />
                                </button>
                                <button type="button" onClick={() => deleteMutation.mutate(h.id)} disabled={deleteMutation.isPending}
                                    className="p-2 rounded hover:bg-red-500/20 text-gray-400 hover:text-red-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400" title="Delete">
                                    <Trash2 size={16} />
                                </button>
                            </div>
                        </li>
                    ))}
                </ul>
            )}
        </div>
    );
}
