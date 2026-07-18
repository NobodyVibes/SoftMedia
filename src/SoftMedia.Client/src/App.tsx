import { useEffect, useState } from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import { Toaster } from 'sonner';
import OfflinePage from './pages/OfflinePage';
import LoginPage from './pages/LoginPage';
import SignupPage from './pages/SignupPage';
import ProtectedRoute from './components/auth/ProtectedRoute';
import { useAuthStore } from './store/authStore';
import { fetchMediaToken } from './services/api';
import MainLayout from './components/layout/MainLayout';
import HomePage from './pages/HomePage';
import LibraryPage from './pages/LibraryPage';
import PlayerPage from './pages/PlayerPage';
import ReaderPage from './pages/ReaderPage';
import SettingsPage from './pages/SettingsPage';
import MyAccountPage from './pages/MyAccountPage';
import MediaDetailPage from './pages/MediaDetailPage';
import PlaylistDetailPage from './pages/PlaylistDetailPage';
import CollectionDetailPage from './pages/CollectionDetailPage';
import { PersistentPlayer } from './components/player/PersistentPlayer';

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
      useAuthStore.getState().setMediaToken(null);
      return;
    }
    void fetchMediaToken();
    if (mediaToken) {
      setConnectAttempts(0);
      return;
    }
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
        <Route path="/signup" element={<SignupPage />} />

        <Route element={<ProtectedRoute />}>
          <Route element={<MainLayout />}>
            <Route path="/" element={<HomePage />} />
            <Route path="/libraries/:id" element={<LibraryPage />} />
            <Route path="/settings" element={
              user?.role === 'Admin'
                ? <Navigate to="/settings/playback/transcoding" replace />
                : <Navigate to="/settings/client/general" replace />
            } />
            <Route path="/settings/:section" element={<SettingsPage />} />
            <Route path="/settings/:section/:subsection" element={<SettingsPage />} />
            <Route path="/account" element={<MyAccountPage />} />
            <Route path="/media/:id" element={<MediaDetailPage />} />
            {/* Playlists index lives inside the Music library as a view-mode
                tab; we keep only the detail route for direct linking. */}
            <Route path="/playlists/:id" element={<PlaylistDetailPage />} />
            <Route path="/collections/:id" element={<CollectionDetailPage />} />
          </Route>
          {/* PlayerPage and ReaderPage are full screen - outside MainLayout */}
          <Route path="/play/:id" element={<PlayerPage />} />
          <Route path="/read/:id" element={<ReaderPage />} />
        </Route>

        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
      <PersistentPlayer />
      <Toaster position="top-right" theme="dark" />
    </>
  );
}

export default App;

