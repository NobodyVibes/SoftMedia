import { Link, useNavigate } from 'react-router-dom';
import { Search, Bell, Menu, ChevronDown, User as UserIcon, Settings, AlertCircle, LogOut, X, AlertTriangle, Info, Loader2 } from 'lucide-react';
import { useAuthStore } from '../../store/authStore';
import { useUIStore } from '../../store/uiStore';
import { motion, AnimatePresence } from 'framer-motion';
import { useState, useRef, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useDebounce } from '../../hooks/useDebounce';
import { searchService } from '../../services/searchService';
import { playlistService } from '../../services/playlistService';
import { notificationService, type SystemNotification } from '../../services/notificationService';
import { libraryService } from '../../services/libraryService';
import type { LibraryScanJob } from '../../types';
import GlobalSearchResults from './GlobalSearchResults';

interface TopBarProps {
    /** SR-WI-040: whether the mobile nav drawer is open (for aria-expanded on the hamburger). */
    isMobileNavOpen?: boolean;
    /** SR-WI-040: opens the mobile nav drawer (hamburger below `md`). */
    onOpenMobileNav?: () => void;
}

export default function TopBar({ isMobileNavOpen = false, onOpenMobileNav }: TopBarProps) {
    const user = useAuthStore((state) => state.user);
    const logout = useAuthStore((state) => state.logout);
    const { toggleSidebar, isSidebarCollapsed } = useUIStore();
    const [isUserMenuOpen, setIsUserMenuOpen] = useState(false);
    const [isNotificationMenuOpen, setIsNotificationMenuOpen] = useState(false);
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    // Search state
    const [searchQuery, setSearchQuery] = useState('');
    const [isSearchFocused, setIsSearchFocused] = useState(false);
    // Below `md` the search input collapses to an icon button that expands into
    // a full-width overlay row across the TopBar (SR-WI-040).
    const [isMobileSearchOpen, setIsMobileSearchOpen] = useState(false);
    const searchContainerRef = useRef<HTMLDivElement>(null);
    const searchInputRef = useRef<HTMLInputElement>(null);
    const notificationContainerRef = useRef<HTMLDivElement>(null);
    const debouncedQuery = useDebounce(searchQuery, 300);

    // Search query
    const { data: searchResults = [], isLoading: isSearching } = useQuery({
        queryKey: ['globalSearch', debouncedQuery],
        queryFn: () => searchService.globalSearch(debouncedQuery),
        enabled: debouncedQuery.length >= 2,
        staleTime: 30000,
    });

    // Playlists are searched separately: they are not media items and belong to no
    // library, so /media/search has nowhere to put them. Its own query so a slow or
    // failed playlist lookup never holds back or blanks the library results.
    const { data: playlistResults = [] } = useQuery({
        queryKey: ['playlistSearch', debouncedQuery],
        queryFn: () => playlistService.search(debouncedQuery),
        enabled: debouncedQuery.length >= 2,
        staleTime: 30000,
    });

    // Library-name matching happens client-side: the list is tiny, already
    // ACL-filtered by the server, and cached app-wide under this key (the
    // sidebar keeps it warm), so finding "Test" the LIBRARY costs nothing.
    const { data: allLibraries = [] } = useQuery({
        queryKey: ['libraries'],
        queryFn: libraryService.getAll,
        staleTime: 30000,
    });

    // Notifications query (admin only)
    const isAdmin = user?.role === 'Admin';
    const { data: notifications = [] } = useQuery({
        queryKey: ['systemNotifications'],
        queryFn: () => notificationService.getNotifications(),
        enabled: isAdmin,
        refetchInterval: 30000, // Poll every 30 seconds
    });

    // Live scan activity (admin only). SignalR invalidates ['scanQueue'] on every scan
    // event, so this updates in near-real-time while mounted; the idle 30s poll catches
    // scans started while the hub was disconnected.
    const { data: scanQueue = [] } = useQuery<LibraryScanJob[]>({
        queryKey: ['scanQueue'],
        queryFn: libraryService.getScanQueue,
        enabled: isAdmin,
        refetchInterval: (query) => {
            const jobs = query.state.data ?? [];
            const active = jobs.some((j: LibraryScanJob) => j.status === 'Running' || j.status === 'Queued');
            return active ? 5000 : 30000;
        },
    });
    const activeScans = scanQueue.filter(j => j.status === 'Running' || j.status === 'Queued');
    const hasActiveScans = activeScans.length > 0;
    const runningScan = activeScans.find(j => j.status === 'Running');
    const scanSummary = runningScan
        ? `${runningScan.libraryName}${runningScan.totalFiles > 0 ? ` — ${runningScan.processedFiles}/${runningScan.totalFiles}` : ''}${activeScans.length > 1 ? ` (+${activeScans.length - 1} more)` : ''}`
        : `${activeScans.length} scan${activeScans.length === 1 ? '' : 's'} queued`;

    // Dismiss mutation
    const dismissMutation = useMutation({
        mutationFn: (id: string) => notificationService.dismissNotification(id),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['systemNotifications'] });
        },
    });

    // Close dropdowns when clicking outside
    useEffect(() => {
        const handleClickOutside = (event: MouseEvent) => {
            if (searchContainerRef.current && !searchContainerRef.current.contains(event.target as Node)) {
                setIsSearchFocused(false);
                setIsMobileSearchOpen(false);
            }
            if (notificationContainerRef.current && !notificationContainerRef.current.contains(event.target as Node)) {
                setIsNotificationMenuOpen(false);
            }
        };
        document.addEventListener('mousedown', handleClickOutside);
        return () => document.removeEventListener('mousedown', handleClickOutside);
    }, []);

    const handleSearchClose = () => {
        setIsSearchFocused(false);
        setIsMobileSearchOpen(false);
        setSearchQuery('');
    };

    const openMobileSearch = () => {
        setIsMobileSearchOpen(true);
        // Focus after the overlay renders the (previously hidden) input.
        requestAnimationFrame(() => searchInputRef.current?.focus());
    };

    const showSearchResults = isSearchFocused && debouncedQuery.length >= 2;
    // The live scan entry counts toward the badge while active; it disappears on its own
    // when the queue drains (it is derived state, not a persisted notification).
    const notificationCount = notifications.length + (hasActiveScans ? 1 : 0);

    const getSeverityIcon = (severity: string) => {
        switch (severity) {
            case 'error': return <AlertCircle size={16} className="text-red-400" />;
            case 'warning': return <AlertTriangle size={16} className="text-amber-400" />;
            default: return <Info size={16} className="text-blue-400" />;
        }
    };

    const getSeverityColor = (severity: string) => {
        switch (severity) {
            case 'error': return 'border-red-500/30 bg-red-500/10';
            case 'warning': return 'border-amber-500/30 bg-amber-500/10';
            default: return 'border-blue-500/30 bg-blue-500/10';
        }
    };

    return (
        <div className="h-16 bg-[#1a1a1a]/95 backdrop-blur-md border-b border-white/5 flex items-center px-3 md:px-6 fixed top-0 left-0 right-0 z-50">
            {/* Left Section: Toggle & Logo (fixed width matching sidebar at md+, compact below) */}
            <div className="flex items-center md:w-64 flex-shrink-0 pr-3 md:pr-6">
                {/* Below md the hamburger opens the nav drawer; at md+ it toggles
                    sidebar collapse exactly as before (two buttons, CSS-swapped). */}
                <button
                    onClick={onOpenMobileNav}
                    aria-label="Open navigation menu"
                    aria-expanded={isMobileNavOpen}
                    className="md:hidden p-2 hover:bg-white/10 rounded-lg transition-colors text-white mr-2"
                >
                    <Menu size={24} />
                </button>
                <button
                    onClick={toggleSidebar}
                    aria-label={isSidebarCollapsed ? 'Expand sidebar' : 'Collapse sidebar'}
                    aria-expanded={!isSidebarCollapsed}
                    className="hidden md:block p-2 hover:bg-white/10 rounded-lg transition-colors text-white mr-4"
                >
                    <Menu size={24} />
                </button>

                <h1 className="text-xl md:text-3xl font-bold text-transparent bg-clip-text bg-gradient-to-r from-blue-400 to-purple-400 tracking-tight drop-shadow-lg whitespace-nowrap">
                    SoftMedia
                </h1>
            </div>

            {/* Search Bar — inline at md+; below md hidden until the search icon
                button expands it into a full-width overlay row across the TopBar. */}
            <div
                ref={searchContainerRef}
                className={
                    isMobileSearchOpen
                        ? "absolute inset-x-0 top-0 h-16 z-20 flex items-center gap-2 bg-[#1a1a1a] px-3 md:static md:h-auto md:z-auto md:bg-transparent md:px-0 md:flex-1 md:max-w-2xl"
                        : "hidden md:block md:flex-1 md:max-w-2xl"
                }
            >
                <div className="relative group flex-1">
                    <Search className="absolute left-4 top-1/2 -translate-y-1/2 text-gray-400 group-focus-within:text-primary transition-colors z-10" size={18} />
                    <input
                        ref={searchInputRef}
                        type="text"
                        value={searchQuery}
                        onChange={(e) => setSearchQuery(e.target.value)}
                        onFocus={() => setIsSearchFocused(true)}
                        onKeyDown={(e) => e.key === 'Escape' && handleSearchClose()}
                        placeholder="Search for movies, TV shows..."
                        className="w-full bg-white/5 border border-white/10 rounded-full py-2.5 pl-11 pr-4 text-sm text-white placeholder:text-gray-500 focus:outline-none focus:border-primary/50 focus:bg-white/10 focus:ring-2 focus:ring-primary/20 transition-all"
                    />

                    {/* Search Results Dropdown */}
                    <AnimatePresence>
                        {showSearchResults && (
                            <GlobalSearchResults
                                results={searchResults}
                                playlists={playlistResults}
                                libraries={allLibraries}
                                query={debouncedQuery}
                                isLoading={isSearching}
                                onClose={handleSearchClose}
                            />
                        )}
                    </AnimatePresence>
                </div>
                {isMobileSearchOpen && (
                    <button
                        onClick={handleSearchClose}
                        aria-label="Close search"
                        className="md:hidden p-2 text-gray-400 hover:text-white hover:bg-white/10 rounded-lg transition-colors flex-shrink-0"
                    >
                        <X size={20} />
                    </button>
                )}
            </div>

            {/* Right Actions */}
            <div className="flex items-center gap-2 md:gap-4 ml-auto">
                {/* Mobile search trigger (input is collapsed below md) */}
                <button
                    onClick={openMobileSearch}
                    aria-label="Open search"
                    aria-expanded={isMobileSearchOpen}
                    className="md:hidden p-2.5 text-gray-400 hover:text-white hover:bg-white/5 rounded-full transition-colors"
                >
                    <Search size={22} />
                </button>

                {/* Notification Bell - Admin Only */}
                {isAdmin && (
                    <div className="relative" ref={notificationContainerRef}>
                        <motion.button
                            onClick={() => setIsNotificationMenuOpen(!isNotificationMenuOpen)}
                            whileHover={{ scale: 1.05 }}
                            whileTap={{ scale: 0.95 }}
                            aria-label={notificationCount > 0 ? `Notifications (${notificationCount})` : 'Notifications'}
                            aria-expanded={isNotificationMenuOpen}
                            className="relative p-2.5 text-gray-400 hover:text-white hover:bg-white/5 rounded-full transition-colors group"
                        >
                            <Bell size={22} className={notificationCount > 0 ? "text-amber-400" : "group-hover:animate-pulse"} />
                            {/* Notification Badge */}
                            {notificationCount > 0 && (
                                <span className="absolute -top-0.5 -right-0.5 min-w-[18px] h-[18px] px-1 bg-red-500 rounded-full ring-2 ring-[#1a1a1a] text-[10px] font-bold text-white flex items-center justify-center">
                                    {notificationCount > 9 ? '9+' : notificationCount}
                                </span>
                            )}
                        </motion.button>

                        {/* Notification Dropdown */}
                        <AnimatePresence>
                            {isNotificationMenuOpen && (
                                <motion.div
                                    initial={{ opacity: 0, y: -10 }}
                                    animate={{ opacity: 1, y: 0 }}
                                    exit={{ opacity: 0, y: -10 }}
                                    transition={{ duration: 0.2 }}
                                    className="absolute right-0 mt-3 w-80 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-2xl overflow-hidden z-50"
                                >
                                    {/* Header */}
                                    <div className="px-4 py-3 border-b border-white/10 flex items-center justify-between">
                                        <h3 className="text-sm font-semibold text-white">System Notifications</h3>
                                        <button
                                            onClick={() => setIsNotificationMenuOpen(false)}
                                            className="p-1 text-gray-400 hover:text-white rounded"
                                        >
                                            <X size={14} />
                                        </button>
                                    </div>

                                    {/* Notifications List */}
                                    <div className="max-h-[300px] overflow-y-auto">
                                        {/* Live scan activity — derived entry, present only while
                                            scans are running/queued; links to the scan status UI. */}
                                        {hasActiveScans && (
                                            <button
                                                onClick={() => {
                                                    setIsNotificationMenuOpen(false);
                                                    navigate('/settings/library/libraries');
                                                }}
                                                className="w-[calc(100%-16px)] mx-2 mt-2 p-3 rounded-lg border border-blue-500/30 bg-blue-500/10 text-left hover:bg-blue-500/20 transition-colors"
                                            >
                                                <div className="flex items-start gap-2">
                                                    <Loader2 size={16} className="text-blue-400 animate-spin mt-0.5 flex-shrink-0" />
                                                    <div className="flex-1 min-w-0">
                                                        <p className="text-sm font-medium text-white">Library scan in progress</p>
                                                        <p className="text-xs text-gray-400 mt-0.5 truncate">{scanSummary}</p>
                                                        <p className="text-xs text-primary mt-1">View scan status →</p>
                                                    </div>
                                                </div>
                                            </button>
                                        )}
                                        {notifications.length === 0 && !hasActiveScans ? (
                                            <div className="p-6 text-center text-gray-500">
                                                <Bell size={24} className="mx-auto mb-2 opacity-30" />
                                                <p className="text-sm">No notifications</p>
                                            </div>
                                        ) : (
                                            <div className="py-2">
                                                {notifications.map((notification: SystemNotification) => (
                                                    <div
                                                        key={notification.id}
                                                        className={`mx-2 mb-2 p-3 rounded-lg border ${getSeverityColor(notification.severity)}`}
                                                    >
                                                        <div className="flex items-start gap-2">
                                                            {getSeverityIcon(notification.severity)}
                                                            <div className="flex-1 min-w-0">
                                                                <p className="text-sm font-medium text-white">{notification.title}</p>
                                                                <p className="text-xs text-gray-400 mt-0.5">{notification.message}</p>
                                                            </div>
                                                            <button
                                                                onClick={() => dismissMutation.mutate(notification.id)}
                                                                className="p-1 text-gray-500 hover:text-white rounded hover:bg-white/10 flex-shrink-0"
                                                                title="Dismiss"
                                                            >
                                                                <X size={12} />
                                                            </button>
                                                        </div>
                                                    </div>
                                                ))}
                                            </div>
                                        )}
                                    </div>

                                    {/* Footer */}
                                    {notifications.length > 0 && (
                                        <div className="px-4 py-2 border-t border-white/10">
                                            <button
                                                onClick={() => {
                                                    setIsNotificationMenuOpen(false);
                                                    // /settings?tab=admin redirected to the role-default
                                                    // settings page; the admin dashboard is a real route.
                                                    navigate('/settings/admin');
                                                }}
                                                className="w-full text-xs text-primary hover:text-primary/80 text-center"
                                            >
                                                View in Admin Dashboard
                                            </button>
                                        </div>
                                    )}
                                </motion.div>
                            )}
                        </AnimatePresence>
                    </div>
                )}

                {/* User Profile Dropdown */}
                <div className="relative pl-4 border-l border-white/10">
                    <motion.button
                        onClick={() => setIsUserMenuOpen(!isUserMenuOpen)}
                        whileHover={{ scale: 1.02 }}
                        whileTap={{ scale: 0.98 }}
                        aria-label="User menu"
                        aria-expanded={isUserMenuOpen}
                        className="flex items-center gap-3 cursor-pointer group"
                    >
                        <div className="text-right hidden sm:block">
                            <p className="text-sm font-semibold text-white group-hover:text-primary transition-colors">
                                {user?.username || 'Guest'}
                            </p>
                            <p className="text-xs text-gray-400 capitalize">
                                {user?.role || 'User'}
                            </p>
                        </div>
                        <div className="relative">
                            <div className="w-10 h-10 rounded-full bg-gradient-to-br from-primary to-secondary flex items-center justify-center text-white font-bold text-xs shadow-lg ring-2 ring-white/10 group-hover:ring-primary/50 transition-all uppercase">
                                {(user?.role && typeof user.role === 'string' ? user.role.charAt(0) : 'U')}
                            </div>
                            {/* Online Indicator */}
                            <span className="absolute bottom-0 right-0 w-3 h-3 bg-green-500 rounded-full ring-2 ring-[#1a1a1a]" />
                        </div>
                        <ChevronDown
                            size={16}
                            className={`text-gray-400 transition-transform duration-200 ${isUserMenuOpen ? 'rotate-180' : ''}`}
                        />
                    </motion.button>

                    {/* Dropdown Menu */}
                    <AnimatePresence>
                        {isUserMenuOpen && (
                            <motion.div
                                initial={{ opacity: 0, y: -10 }}
                                animate={{ opacity: 1, y: 0 }}
                                exit={{ opacity: 0, y: -10 }}
                                transition={{ duration: 0.2 }}
                                className="absolute right-0 mt-3 w-56 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-2xl overflow-hidden z-50"
                            >
                                {/* User Info Header */}
                                <div className="px-4 py-3 border-b border-white/10 bg-gradient-to-r from-primary/10 to-secondary/10">
                                    <p className="text-sm font-semibold text-white">{user?.username || 'Guest'}</p>
                                    <p className="text-xs text-gray-400 mt-1">
                                        <span className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full bg-primary/20 text-primary text-xs font-medium capitalize">
                                            {user?.role || 'User'}
                                        </span>
                                    </p>
                                </div>

                                {/* Menu Items — the dead "Report Issues"/"Help"/"Switch User"
                                    placeholders were removed (SR-WI-040/050); "View Profile"
                                    goes to the account page (the profile/settings route). */}
                                <div className="py-2">
                                    <Link
                                        to="/account"
                                        onClick={() => setIsUserMenuOpen(false)}
                                        className="w-full px-4 py-2.5 flex items-center gap-3 text-sm text-gray-300 hover:bg-white/5 hover:text-white transition-colors group"
                                    >
                                        <UserIcon size={16} className="text-gray-400 group-hover:text-primary transition-colors" />
                                        <span>View Profile</span>
                                    </Link>
                                    <Link
                                        to="/account"
                                        onClick={() => setIsUserMenuOpen(false)}
                                        className="w-full px-4 py-2.5 flex items-center gap-3 text-sm text-gray-300 hover:bg-white/5 hover:text-white transition-colors group"
                                    >
                                        <Settings size={16} className="text-gray-400 group-hover:text-primary transition-colors" />
                                        <span>My Account</span>
                                    </Link>
                                </div>

                                {/* Sign Out */}
                                <div className="border-t border-white/10 py-2">
                                    <button
                                        onClick={logout}
                                        className="w-full px-4 py-2.5 flex items-center gap-3 text-sm text-red-400 hover:bg-red-500/10 hover:text-red-300 transition-colors group"
                                    >
                                        <LogOut size={16} className="group-hover:translate-x-0.5 transition-transform" />
                                        <span>Sign Out</span>
                                    </button>
                                </div>
                            </motion.div>
                        )}
                    </AnimatePresence>
                </div>
            </div>
        </div>
    );
}
