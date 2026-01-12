import { Link, useLocation } from 'react-router-dom';
import { Home, Film, Tv, Music, Book, LogOut, Gamepad2, Image, Settings } from 'lucide-react';
import { useAuthStore } from '../../store/authStore';
import { useUIStore } from '../../store/uiStore';
import { useLibraries } from '../../hooks/useLibrary';
import { cn } from '../../lib/utils';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';

const libraryTypeIcons: Record<string, any> = {
    Movie: Film,
    TV: Tv,
    Music: Music,
    Book: Book,
    Game: Gamepad2,
    Photo: Image,
};

export default function Sidebar() {
    const location = useLocation();
    const logout = useAuthStore((state) => state.logout);
    const { isSidebarCollapsed } = useUIStore();
    const { data: libraries } = useLibraries();
    const { t } = useTranslation();

    const navItems = [
        { name: t('Home'), path: '/', icon: Home, isStatic: true },
        ...(libraries || []).map(lib => ({
            name: lib.name, // Library names are dynamic, might not be translated
            path: `/libraries/${lib.id}`,
            icon: libraryTypeIcons[lib.type] || Film,
            isStatic: false,
        })),
        { name: t('Settings'), path: '/settings', icon: Settings, isStatic: true },
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
                                {/* Active Indicator */}
                                {isActive && (
                                    <motion.div
                                        layoutId="activeTab"
                                        className={cn(
                                            "absolute top-1/2 -translate-y-1/2 bg-brand-gradient rounded-r-full",
                                            isSidebarCollapsed ? "left-0 w-1 h-8" : "left-0 w-1 h-8"
                                        )}
                                        transition={{ type: "spring", stiffness: 300, damping: 30 }}
                                    />
                                )}

                                {/* Icon */}
                                <div className="relative flex-shrink-0">
                                    <Icon
                                        size={24}
                                        className={cn(
                                            "transition-all",
                                            isActive
                                                ? "text-primary drop-shadow-[0_0_8px_rgba(99,102,241,0.6)]"
                                                : ""
                                        )}
                                    />
                                </div>

                                {!isSidebarCollapsed && (
                                    <motion.span
                                        initial={{ opacity: 0, width: 0 }}
                                        animate={{ opacity: 1, width: 'auto' }}
                                        exit={{ opacity: 0, width: 0 }}
                                        className={cn(
                                            "font-semibold text-sm transition-all whitespace-nowrap",
                                            isActive && "drop-shadow-[0_0_10px_rgba(99,102,241,0.3)]"
                                        )}
                                    >
                                        {item.name}
                                    </motion.span>
                                )}

                                {/* Hover Glow */}
                                <div className="absolute inset-0 bg-gradient-to-r from-primary/0 via-primary/5 to-primary/0 opacity-0 group-hover:opacity-100 transition-opacity rounded-xl" />
                            </Link>
                        </motion.div>
                    );
                })}


            </nav>

            {/* Logout Button */}
            <div className="p-3 border-t border-white/5 bg-gradient-to-t from-black/20 to-transparent border-r border-white/5">
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
