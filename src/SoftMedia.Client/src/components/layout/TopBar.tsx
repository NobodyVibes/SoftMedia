import { Link } from 'react-router-dom';
import { Search, Bell, Menu, ChevronDown, User as UserIcon, Settings, AlertCircle, HelpCircle, Users, LogOut } from 'lucide-react';
import { useAuthStore } from '../../store/authStore';
import { useUIStore } from '../../store/uiStore';
import { motion, AnimatePresence } from 'framer-motion';
import { useState } from 'react';

export default function TopBar() {
    const user = useAuthStore((state) => state.user);
    const logout = useAuthStore((state) => state.logout);
    const { toggleSidebar } = useUIStore();
    const [isUserMenuOpen, setIsUserMenuOpen] = useState(false);

    return (
        <div className="h-16 bg-[#1a1a1a]/95 backdrop-blur-md border-b border-white/5 flex items-center px-6 fixed top-0 left-0 right-0 z-50">
            {/* Left Section: Toggle & Logo (Fixed Width matching Sidebar) */}
            <div className="flex items-center w-64 flex-shrink-0 pr-6">
                <button
                    onClick={toggleSidebar}
                    className="p-2 hover:bg-white/10 rounded-lg transition-colors text-white mr-4"
                >
                    <Menu size={24} />
                </button>

                <h1 className="text-3xl font-bold text-transparent bg-clip-text bg-gradient-to-r from-blue-400 to-purple-400 tracking-tight drop-shadow-lg whitespace-nowrap">
                    SoftMedia
                </h1>
            </div>

            {/* Search Bar */}
            <div className="flex-1 max-w-2xl">
                <div className="relative group">
                    <Search className="absolute left-4 top-1/2 -translate-y-1/2 text-gray-400 group-focus-within:text-primary transition-colors" size={18} />
                    <input
                        type="text"
                        placeholder="Search for movies, TV shows, actors..."
                        className="w-full bg-white/5 border border-white/10 rounded-full py-2.5 pl-11 pr-4 text-sm text-white placeholder:text-gray-500 focus:outline-none focus:border-primary/50 focus:bg-white/10 focus:ring-2 focus:ring-primary/20 transition-all"
                    />
                </div>
            </div>

            {/* Right Actions */}
            <div className="flex items-center gap-4 ml-auto">
                {/* Notification Bell */}
                <motion.button
                    whileHover={{ scale: 1.05 }}
                    whileTap={{ scale: 0.95 }}
                    className="relative p-2.5 text-gray-400 hover:text-white hover:bg-white/5 rounded-full transition-colors group"
                >
                    <Bell size={22} className="group-hover:animate-pulse" />
                    {/* Notification Badge */}
                    <span className="absolute top-1 right-1 w-2 h-2 bg-red-500 rounded-full ring-2 ring-[#1a1a1a] animate-pulse" />
                </motion.button>

                {/* User Profile Dropdown */}
                <div className="relative pl-4 border-l border-white/10">
                    <motion.button
                        onClick={() => setIsUserMenuOpen(!isUserMenuOpen)}
                        whileHover={{ scale: 1.02 }}
                        whileTap={{ scale: 0.98 }}
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

                                {/* Menu Items */}
                                <div className="py-2">
                                    <button className="w-full px-4 py-2.5 flex items-center gap-3 text-sm text-gray-300 hover:bg-white/5 hover:text-white transition-colors group">
                                        <UserIcon size={16} className="text-gray-400 group-hover:text-primary transition-colors" />
                                        <span>View Profile</span>
                                    </button>
                                    <Link to="/settings" className="w-full px-4 py-2.5 flex items-center gap-3 text-sm text-gray-300 hover:bg-white/5 hover:text-white transition-colors group">
                                        <Settings size={16} className="text-gray-400 group-hover:text-primary transition-colors" />
                                        <span>Account Settings</span>
                                    </Link>
                                    <button className="w-full px-4 py-2.5 flex items-center gap-3 text-sm text-gray-300 hover:bg-white/5 hover:text-white transition-colors group">
                                        <AlertCircle size={16} className="text-gray-400 group-hover:text-primary transition-colors" />
                                        <span>Report Issues</span>
                                    </button>
                                    <button className="w-full px-4 py-2.5 flex items-center gap-3 text-sm text-gray-300 hover:bg-white/5 hover:text-white transition-colors group">
                                        <HelpCircle size={16} className="text-gray-400 group-hover:text-primary transition-colors" />
                                        <span>Help</span>
                                    </button>
                                </div>

                                {/* Switch User & Sign Out */}
                                <div className="border-t border-white/10 py-2">
                                    <button className="w-full px-4 py-2.5 flex items-center gap-3 text-sm text-gray-300 hover:bg-white/5 hover:text-white transition-colors group">
                                        <Users size={16} className="text-gray-400 group-hover:text-primary transition-colors" />
                                        <span>Switch User</span>
                                    </button>
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
