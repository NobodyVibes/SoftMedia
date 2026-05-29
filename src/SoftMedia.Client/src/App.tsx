import { useEffect, useState } from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import { Toaster } from 'sonner';
import OfflinePage from './pages/OfflinePage';
import LoginPage from './pages/LoginPage';
import SignupPage from './pages/SignupPage';
import ProtectedRoute from './components/auth/ProtectedRoute';
import { useAuthStore } from './store/authStore';
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

