import { Outlet } from 'react-router-dom';
import Sidebar from './Sidebar';
import TopBar from './TopBar';
import { useUIStore } from '../../store/uiStore';
import { useAudioStore } from '../../store/audioStore';
import { cn } from '../../lib/utils';
import ScanProgressToast from '../shared/ScanProgressToast';

export default function MainLayout() {
    const { isSidebarCollapsed } = useUIStore();
    const currentTrack = useAudioStore(s => s.currentTrack);

    return (
        <div className="min-h-screen bg-background text-white font-sans">
            <TopBar />
            <ScanProgressToast />

            <div className="flex pt-16">
                <Sidebar />

                <main className={cn(
                    "flex-1 overflow-y-auto h-[calc(100vh-4rem)] transition-all duration-300 ease-in-out",
                    isSidebarCollapsed ? "ml-20" : "ml-64",
                    currentTrack ? "pb-24" : "pb-4"
                )}>
                    <Outlet />
                </main>
            </div>
        </div>
    );
}
