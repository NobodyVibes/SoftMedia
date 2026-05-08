import { useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { Home, Film, Tv, Music, Book, LogOut, Gamepad2, Image, Settings, ChevronRight, ChevronDown, Play, Database, Users, ShieldCheck, User } from 'lucide-react';
import { useAuthStore } from '../../store/authStore';
import { useUIStore } from '../../store/uiStore';
import { useLibraries } from '../../hooks/useLibrary';
import { cn } from '../../lib/utils';
import { motion, AnimatePresence } from 'framer-motion';
import { useTranslation } from 'react-i18next';

const libraryTypeIcons: Record<string, any> = {
    Movie: Film,
    TV: Tv,
    Music: Music,
    Book: Book,
    Game: Gamepad2,
    Photo: Image,
};

// Settings navigation tree structure with URL paths
const settingsNavTree = [
    {
        id: 'client',
        label: 'Client Settings',
        icon: User,
        path: '/settings/client/general',
        children: [
            { id: 'general', label: 'General', path: '/settings/client/general' },
            { id: 'playback-client', label: 'Playback', path: '/settings/client/playback' },
            { id: 'audio', label: 'Audio', path: '/settings/client/audio' },
        ]
    },
    {
        id: 'playback',
        label: 'Server Playback',
        icon: Play,
        path: '/settings/playback/transcoding',
        adminOnly: true,
        children: [
            { id: 'transcoding', label: 'Transcoding', path: '/settings/playback/transcoding' },
            { id: 'streaming', label: 'Streaming Quality', path: '/settings/playback/streaming' },
        ]
    },
    {
        id: 'library',
        label: 'Library Management',
        icon: Database,
        path: '/settings/library/libraries',
        adminOnly: true,
        children: [
            { id: 'libraries', label: 'Libraries', path: '/settings/library/libraries' },
            { id: 'metadata', label: 'Metadata Providers', path: '/settings/library/metadata-providers' },
        ]
    },
    { id: 'users', label: 'Account Management', icon: Users, path: '/settings/users', adminOnly: true },
    { id: 'admin', label: 'Admin Dashboard', icon: ShieldCheck, path: '/settings/admin', adminOnly: true },
];

export default function Sidebar() {
    const location = useLocation();
    const logout = useAuthStore((state) => state.logout);
    const user = useAuthStore((state) => state.user);
    const { isSidebarCollapsed } = useUIStore();
    const { data: libraries } = useLibraries();
    const { t } = useTranslation();

    const isAdmin = user?.role === 'Admin';
    const isOnSettingsPage = location.pathname.startsWith('/settings');

    // Expand state for settings sub-categories
    const [expandedCategories, setExpandedCategories] = useState<Set<string>>(new Set(['playback', 'library']));

    const toggleCategory = (categoryId: string) => {
        setExpandedCategories(prev => {
            const next = new Set(prev);
            if (next.has(categoryId)) {
                next.delete(categoryId);
            } else {
                next.add(categoryId);
            }
            return next;
        });
    };

    const isSettingsItemActive = (item: typeof settingsNavTree[0]): boolean => {
        if (item.children) {
            return item.children.some(child => location.pathname === child.path);
        }
        return location.pathname === item.path;
    };

    const navItems = [
        { name: t('Home'), path: '/', icon: Home, isStatic: true },
        ...(libraries || []).map(lib => ({
            name: lib.name,
            path: `/libraries/${lib.id}`,
            icon: libraryTypeIcons[lib.type] || Film,
            isStatic: false,
        })),
        // Playlists live inside each Music library as a view-mode tab
        // (Albums / Artists / Tracks / Playlists). They were previously a
        // top-level sidebar entry but that misrepresented their scope —
        // playlists are music-only in v1, so a global nav item is the
        // wrong affordance. Direct links to /playlists/<id> still work.
    ];

    return (
        <motion.div
            initial={false}
            animate={{ width: isSidebarCollapsed ? 80 : 256 }}
            className="h-[calc(100vh-4rem)] bg-gradient-to-b from-[#1a1a1a] to-[#0f0f0f] flex flex-col fixed left-0 top-16 z-40 shadow-2xl overflow-hidden border-r border-white/5"
        >
            {/* Navigation */}
            <nav className="flex-1 px-3 py-6 space-y-1.5 overflow-y-auto overflow-x-hidden">
                {!isSidebarCollapsed && (
                    <motion.div
                        initial={{ opacity: 0 }}
                        animate={{ opacity: 1 }}
                        className="text-xs font-bold text-gray-500 uppercase tracking-wider px-4 mb-3 whitespace-nowrap"
                    >
                        Browse
                    </motion.div>
                )}

                {navItems.map((item, index) => {
                    const Icon = item.icon;
                    const isActive = location.pathname === item.path;

                    return (
                        <motion.div
                            key={item.path}
                            initial={{ opacity: 0, x: -10 }}
                            animate={{ opacity: 1, x: 0 }}
                            transition={{ delay: index * 0.05 }}
                        >
                            <Link
                                to={item.path}
                                className={cn(
                                    "relative flex items-center gap-4 px-4 py-3 rounded-xl transition-all group overflow-hidden",
                                    isActive
                                        ? "bg-white/10 text-white shadow-lg"
                                        : "text-gray-400 hover:bg-white/5 hover:text-white",
                                    isSidebarCollapsed && "justify-center px-2"
                                )}
                                title={isSidebarCollapsed ? item.name : undefined}
                            >
                                {isActive && (
                                    <motion.div
                                        layoutId="activeTab"
                                        className="absolute left-0 top-1/2 -translate-y-1/2 bg-brand-gradient rounded-r-full w-1 h-8"
                                        transition={{ type: "spring", stiffness: 300, damping: 30 }}
                                    />
                                )}
                                <div className="relative flex-shrink-0">
                                    <Icon
                                        size={24}
                                        className={cn(
                                            "transition-all",
                                            isActive && "text-primary drop-shadow-[0_0_8px_rgba(99,102,241,0.6)]"
                                        )}
                                    />
                                </div>
                                {!isSidebarCollapsed && (
                                    <motion.span
                                        initial={{ opacity: 0, width: 0 }}
                                        animate={{ opacity: 1, width: 'auto' }}
                                        className={cn(
                                            "font-semibold text-sm transition-all whitespace-nowrap",
                                            isActive && "drop-shadow-[0_0_10px_rgba(99,102,241,0.3)]"
                                        )}
                                    >
                                        {item.name}
                                    </motion.span>
                                )}
                                <div className="absolute inset-0 bg-gradient-to-r from-primary/0 via-primary/5 to-primary/0 opacity-0 group-hover:opacity-100 transition-opacity rounded-xl" />
                            </Link>
                        </motion.div>
                    );
                })}

                {/* Settings Section */}
                {true && (
                    <>
                        {!isSidebarCollapsed && (
                            <motion.div
                                initial={{ opacity: 0 }}
                                animate={{ opacity: 1 }}
                                className="text-xs font-bold text-gray-500 uppercase tracking-wider px-4 mt-6 mb-3 whitespace-nowrap"
                            >
                                Settings
                            </motion.div>
                        )}

                        {/* Settings main link */}
                        <motion.div
                            initial={{ opacity: 0, x: -10 }}
                            animate={{ opacity: 1, x: 0 }}
                        >
                            <Link
                                to="/settings/server"
                                className={cn(
                                    "relative flex items-center gap-4 px-4 py-3 rounded-xl transition-all group overflow-hidden",
                                    isOnSettingsPage
                                        ? "bg-white/10 text-white shadow-lg"
                                        : "text-gray-400 hover:bg-white/5 hover:text-white",
                                    isSidebarCollapsed && "justify-center px-2"
                                )}
                                title={isSidebarCollapsed ? t('Settings') : undefined}
                            >
                                {isOnSettingsPage && (
                                    <motion.div
                                        className="absolute left-0 top-1/2 -translate-y-1/2 bg-brand-gradient rounded-r-full w-1 h-8"
                                    />
                                )}
                                <Settings
                                    size={24}
                                    className={cn(
                                        "transition-all flex-shrink-0",
                                        isOnSettingsPage && "text-primary drop-shadow-[0_0_8px_rgba(99,102,241,0.6)]"
                                    )}
                                />
                                {!isSidebarCollapsed && (
                                    <span className={cn(
                                        "font-semibold text-sm whitespace-nowrap",
                                        isOnSettingsPage && "drop-shadow-[0_0_10px_rgba(99,102,241,0.3)]"
                                    )}>
                                        {t('Settings')}
                                    </span>
                                )}
                            </Link>
                        </motion.div>

                        {/* Settings sub-navigation */}
                        <AnimatePresence>
                            {isOnSettingsPage && !isSidebarCollapsed && (
                                <motion.div
                                    initial={{ opacity: 0, height: 0 }}
                                    animate={{ opacity: 1, height: 'auto' }}
                                    exit={{ opacity: 0, height: 0 }}
                                    className="ml-4 pl-3 border-l border-white/10 space-y-1 mt-1"
                                >
                                    {settingsNavTree
                                        .filter(item => !item.adminOnly || isAdmin)
                                        .map((item) => {
                                            const Icon = item.icon;
                                            const hasChildren = item.children && item.children.length > 0;
                                            const isExpanded = expandedCategories.has(item.id);
                                            const isActive = isSettingsItemActive(item);

                                            return (
                                                <div key={item.id}>
                                                    {hasChildren ? (
                                                        <button
                                                            onClick={() => toggleCategory(item.id)}
                                                            className={cn(
                                                                "w-full flex items-center gap-2 px-3 py-2 rounded-lg text-sm transition-all text-left",
                                                                isActive
                                                                    ? "bg-primary/10 text-primary"
                                                                    : "text-gray-500 hover:bg-white/5 hover:text-white"
                                                            )}
                                                        >
                                                            <span className="w-3 flex-shrink-0">
                                                                {isExpanded ? (
                                                                    <ChevronDown size={12} />
                                                                ) : (
                                                                    <ChevronRight size={12} />
                                                                )}
                                                            </span>
                                                            {Icon && <Icon size={16} className="flex-shrink-0" />}
                                                            <span className="flex-1 truncate">{t(item.label)}</span>
                                                        </button>
                                                    ) : (
                                                        <Link
                                                            to={item.path}
                                                            className={cn(
                                                                "w-full flex items-center gap-2 px-3 py-2 rounded-lg text-sm transition-all text-left",
                                                                isActive
                                                                    ? "bg-primary/10 text-primary"
                                                                    : "text-gray-500 hover:bg-white/5 hover:text-white"
                                                            )}
                                                        >
                                                            {Icon && <Icon size={16} className="flex-shrink-0" />}
                                                            <span className="flex-1 truncate">{t(item.label)}</span>
                                                        </Link>
                                                    )}

                                                    {hasChildren && isExpanded && (
                                                        <div className="ml-5 pl-3 border-l border-white/5 space-y-0.5 mt-0.5">
                                                            {item.children!.map((child) => (
                                                                <Link
                                                                    key={child.id}
                                                                    to={child.path}
                                                                    className={cn(
                                                                        "w-full flex items-center px-3 py-1.5 rounded text-xs transition-all text-left block",
                                                                        location.pathname === child.path
                                                                            ? "bg-primary/10 text-primary"
                                                                            : "text-gray-500 hover:bg-white/5 hover:text-white"
                                                                    )}
                                                                >
                                                                    {t(child.label)}
                                                                </Link>
                                                            ))}
                                                        </div>
                                                    )}
                                                </div>
                                            );
                                        })}
                                </motion.div>
                            )}
                        </AnimatePresence>
                    </>
                )}
            </nav>

            {/* Logout Button */}
            <div className="p-3 border-t border-white/5 bg-gradient-to-t from-black/20 to-transparent">
                <motion.button
                    onClick={logout}
                    whileHover={{ scale: 1.02 }}
                    whileTap={{ scale: 0.98 }}
                    className={cn(
                        "flex items-center gap-4 px-4 py-3 w-full text-gray-400 hover:text-red-400 hover:bg-red-500/10 rounded-xl transition-all group border border-transparent hover:border-red-500/20",
                        isSidebarCollapsed && "justify-center px-2"
                    )}
                    title={isSidebarCollapsed ? t("Sign Out") : undefined}
                >
                    <LogOut size={24} className="group-hover:-translate-x-1 transition-transform flex-shrink-0" />
                    {!isSidebarCollapsed && <span className="font-semibold text-sm whitespace-nowrap">{t('Sign Out')}</span>}
                </motion.button>
            </div>
        </motion.div>
    );
}

