import { lazy, Suspense, useEffect, useState, type ReactNode } from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import { Toaster } from 'sonner';
import OfflinePage from './pages/OfflinePage';
import LoginPage from './pages/LoginPage';
import ProtectedRoute from './components/auth/ProtectedRoute';
import { useAuthStore } from './store/authStore';
import { fetchMediaToken, cancelMediaTokenRenewal } from './services/api';

// Route-level code splitting (SR-WI-041). Everything below the login wall is
// lazy so the initial chunk carries only the entry shell: heavyweights like
// epubjs/react-pdf (ReaderPage), hls.js (PlayerPage), SignalR (MainLayout's
// hub) and the admin settings surface load on first navigation, not before
// the login page can paint. LoginPage and OfflinePage stay eager — they ARE
// the cold-start screens, and a spinner-behind-a-spinner helps nobody.
// IMPORTANT: none of these modules may ALSO be imported statically anywhere,
// or the code lands in both the initial and the lazy chunk.
const SignupPage = lazy(() => import('./pages/SignupPage'));
const MainLayout = lazy(() => import('./components/layout/MainLayout'));
const HomePage = lazy(() => import('./pages/HomePage'));
const LibraryPage = lazy(() => import('./pages/LibraryPage'));
const BrowsePage = lazy(() => import('./pages/BrowsePage'));
const PlayerPage = lazy(() => import('./pages/PlayerPage'));
const ReaderPage = lazy(() => import('./pages/ReaderPage'));
const SettingsPage = lazy(() => import('./pages/SettingsPage'));
const MyAccountPage = lazy(() => import('./pages/MyAccountPage'));
const MediaDetailPage = lazy(() => import('./pages/MediaDetailPage'));
const PlaylistDetailPage = lazy(() => import('./pages/PlaylistDetailPage'));
const CollectionDetailPage = lazy(() => import('./pages/CollectionDetailPage'));
// Rendered unconditionally at the root, so its chunk starts loading right after
// the shell mounts — but off the critical path, behind a null fallback.
const PersistentPlayer = lazy(() =>
  import('./components/player/PersistentPlayer').then((m) => ({ default: m.PersistentPlayer }))
);

/** Branded chunk-loading fallback, consistent with the app's existing spinners. */
function RouteFallback() {
  return (
    <div className="min-h-screen flex items-center justify-center bg-background">
      <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary" />
    </div>
  );
}

/**
 * Per-route Suspense boundary. Placed on each route element (not just around
 * <Routes>) so navigating between pages inside MainLayout suspends only the
 * content area — the sidebar/topbar stay mounted instead of blanking while a
 * page chunk downloads.
 */
function page(node: ReactNode) {
  return <Suspense fallback={<RouteFallback />}>{node}</Suspense>;
}

function App() {
  const user = useAuthStore((state: any) => state.user);
  const token = useAuthStore((state: any) => state.token);
  const mediaToken = useAuthStore((state: any) => state.mediaToken);

  // Media-token lifecycle (audit H3 → WS-6 T6.2 hard dependency): whenever the access
  // token appears or rotates (initial login, persisted session, silent refresh), fetch
  // the reduced-privilege media token; clear it on logout. The server now REJECTS full
  // access tokens in media-URL query strings (T6.1), so there is no fallback — the
  // authed UI below is gated until the media token resolves, and this effect retries
  // while it hasn't (e.g. the server was briefly unreachable on a cold load).
  // connectAttempts drives the gate's escape hatch below: a non-401 failure
  // (server down, proxy broken) never trips the axios refresh/logout path, so
  // without it the spinner would be inescapable.
  const [connectAttempts, setConnectAttempts] = useState(0);
  useEffect(() => {
    if (!token) {
      cancelMediaTokenRenewal();
      useAuthStore.getState().setMediaToken(null);
      return;
    }
    // Already holding one: nothing to do. Renewal ahead of expiry is owned by the
    // timer inside fetchMediaToken, NOT by this effect.
    //
    // The fetch below must stay behind this guard. `mediaToken` is a dependency,
    // and the server mints a BRAND-NEW token on every call — so an unconditional
    // fetch here stored a different value, retriggered this effect, and fetched
    // again, looping for as long as the tab stayed open.
    if (mediaToken) {
      setConnectAttempts(0);
      return;
    }
    void fetchMediaToken();
    const retry = setInterval(() => {
      setConnectAttempts((n) => n + 1);
      void fetchMediaToken();
    }, 4000);
    return () => clearInterval(retry);
  }, [token, mediaToken]);

  // Offline shell (P2-WI-003): when the browser reports no connectivity, show the
  // branded offline screen instead of letting fetches fail silently. The PWA service
  // worker keeps the app shell available so this still renders with no server.
  const [isOnline, setIsOnline] = useState(navigator.onLine);
  useEffect(() => {
    const on = () => setIsOnline(true);
    const off = () => setIsOnline(false);
    window.addEventListener('online', on);
    window.addEventListener('offline', off);
    return () => {
      window.removeEventListener('online', on);
      window.removeEventListener('offline', off);
    };
  }, []);

  if (!isOnline) return <OfflinePage />;

  // WS-6 T6.2: with a session but no media token yet (cold load, ~1 round-trip),
  // hold the authed UI — every media URL and the hub handshake depend on it, and
  // rendering early would spray guaranteed-401 requests. Logged-out routes
  // (login/signup) don't depend on it and render normally.
  if (token && !mediaToken) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background">
        <div className="text-center">
          <div className="w-10 h-10 mx-auto mb-4 border-2 border-primary border-t-transparent rounded-full animate-spin" />
          <p className="text-sm text-gray-400">Connecting to your library…</p>
          {/* Escape hatch (review MED): a network-level failure produces no 401,
              so the axios logout path never fires — after ~12s of retries, offer
              a way out instead of an inescapable spinner. Retries continue. */}
          {connectAttempts >= 3 && (
            <div className="mt-6">
              <p className="text-xs text-gray-500 mb-3">
                The server isn't responding. Retrying automatically…
              </p>
              <button
                type="button"
                onClick={() => useAuthStore.getState().logout()}
                className="px-4 py-2 text-sm rounded-lg text-gray-300 bg-white/10 hover:bg-white/20 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
              >
                Log out
              </button>
            </div>
          )}
        </div>
      </div>
    );
  }

  return (
    <>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/signup" element={page(<SignupPage />)} />

        <Route element={<ProtectedRoute />}>
          <Route element={page(<MainLayout />)}>
            <Route path="/" element={page(<HomePage />)} />
            <Route path="/libraries/:id" element={page(<LibraryPage />)} />
            {/* Cross-library filtered grid. Criteria ride in the query string
                (?genre=&decade=&unplayed=), so home rows can hand over their own
                filter and the resulting view is shareable. */}
            <Route path="/browse" element={page(<BrowsePage />)} />
            <Route path="/settings" element={
              user?.role === 'Admin'
                ? <Navigate to="/settings/playback/transcoding" replace />
                : <Navigate to="/settings/client/general" replace />
            } />
            <Route path="/settings/:section" element={page(<SettingsPage />)} />
            <Route path="/settings/:section/:subsection" element={page(<SettingsPage />)} />
            <Route path="/account" element={page(<MyAccountPage />)} />
            <Route path="/media/:id" element={page(<MediaDetailPage />)} />
            {/* Playlists index lives inside the Music library as a view-mode
                tab; we keep only the detail route for direct linking. */}
            <Route path="/playlists/:id" element={page(<PlaylistDetailPage />)} />
            <Route path="/collections/:id" element={page(<CollectionDetailPage />)} />
          </Route>
          {/* PlayerPage and ReaderPage are full screen - outside MainLayout */}
          <Route path="/play/:id" element={page(<PlayerPage />)} />
          <Route path="/read/:id" element={page(<ReaderPage />)} />
        </Route>

        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
      {/* Fallback null: the player renders nothing until a track plays anyway. */}
      <Suspense fallback={null}>
        <PersistentPlayer />
      </Suspense>
      <Toaster position="top-right" theme="dark" />
    </>
  );
}

export default App;

