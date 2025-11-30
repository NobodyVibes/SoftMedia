import { Routes, Route, Navigate } from 'react-router-dom';
import LoginPage from './pages/LoginPage';
import SignupPage from './pages/SignupPage';
import ProtectedRoute from './components/auth/ProtectedRoute';
import MainLayout from './components/layout/MainLayout';
import HomePage from './pages/HomePage';
import LibraryPage from './pages/LibraryPage';
import PlayerPage from './pages/PlayerPage';
import ReaderPage from './pages/ReaderPage';

import { PersistentPlayer } from './components/player/PersistentPlayer';

function App() {
  return (
    <>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/signup" element={<SignupPage />} />

        <Route element={<ProtectedRoute />}>
          <Route element={<MainLayout />}>
            <Route path="/" element={<HomePage />} />
            <Route path="/libraries/:id" element={<LibraryPage />} />
          </Route>
          {/* PlayerPage might want to be full screen, so maybe outside MainLayout or handle internally */}
          <Route path="/media/:id" element={<PlayerPage />} />
          <Route path="/read/:id" element={<ReaderPage />} />
        </Route>

        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
      <PersistentPlayer />
    </>
  );
}

export default App;
