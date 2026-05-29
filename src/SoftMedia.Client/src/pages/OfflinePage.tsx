import { WifiOff, RefreshCw } from 'lucide-react';

/**
 * Branded offline screen (P2-WI-003). Shown by the offline detector when the app
 * loads (or is navigated) without a network connection. The PWA service worker
 * serves the cached app shell so this renders even with no server reachable.
 */
export default function OfflinePage() {
    return (
        <div className="min-h-screen flex flex-col items-center justify-center text-center px-6"
            style={{ background: 'linear-gradient(135deg, #0f172a, #1a1a2e)' }}>
            <div className="w-20 h-20 rounded-2xl flex items-center justify-center mb-6"
                style={{ background: 'linear-gradient(135deg, #007AFF, #8A2BE2)' }}>
                <WifiOff className="w-10 h-10 text-white" />
            </div>
            <h1 className="text-2xl font-bold text-white mb-2">You're offline</h1>
            <p className="text-gray-400 max-w-sm mb-6">
                SoftMedia can't reach your server right now. Check your connection — your
                media streams from your own server, so it needs to be online.
            </p>
            <button
                type="button"
                onClick={() => window.location.reload()}
                className="inline-flex items-center gap-2 px-5 py-2.5 rounded-lg text-white font-medium transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                style={{ background: 'linear-gradient(135deg, #007AFF, #8A2BE2)' }}
            >
                <RefreshCw className="w-4 h-4" />
                Retry
            </button>
        </div>
    );
}
