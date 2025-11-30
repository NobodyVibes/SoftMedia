import { useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { useAuthStore } from '../store/authStore';
import api from '../services/api';
import { Input } from '../components/ui/Input';
import { Button } from '../components/ui/Button';

export default function LoginPage() {
    const navigate = useNavigate();
    const login = useAuthStore((state) => state.login);

    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [error, setError] = useState('');
    const [isLoading, setIsLoading] = useState(false);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError('');
        setIsLoading(true);

        try {
            const response = await api.post('/auth/login', { username, password });
            const { accessToken: token, user } = response.data;

            login(user, token);
            navigate('/');
        } catch (err: any) {
            console.error(err);
            setError(err.response?.data?.message || 'Invalid username or password');
        } finally {
            setIsLoading(false);
        }
    };

    return (
        <div className="min-h-screen flex items-center justify-center relative overflow-hidden bg-background">
            {/* Background Effects */}
            <div className="absolute inset-0 bg-[radial-gradient(ellipse_at_top_right,_var(--tw-gradient-stops))] from-primary/20 via-background to-background" />
            <div className="absolute -top-40 -right-40 w-96 h-96 bg-primary/30 rounded-full blur-3xl opacity-20 animate-pulse" />
            <div className="absolute -bottom-40 -left-40 w-96 h-96 bg-secondary/30 rounded-full blur-3xl opacity-20 animate-pulse delay-1000" />

            {/* Glassmorphism Card */}
            <div className="w-full max-w-md p-8 rounded-2xl bg-white/5 backdrop-blur-xl border border-white/10 shadow-2xl relative z-10">
                <div className="text-center mb-8">
                    <h1 className="text-4xl font-bold bg-clip-text text-transparent bg-brand-gradient mb-2">
                        SoftMedia
                    </h1>
                    <h2 className="text-xl text-white font-medium">Welcome Back</h2>
                    <p className="text-sm text-gray-400 mt-2">
                        Sign in to access your personal media library
                    </p>
                </div>

                <form className="space-y-6" onSubmit={handleSubmit}>
                    <div className="space-y-4">
                        <div className="space-y-2">
                            <label className="text-sm font-medium text-gray-300 ml-1">Username</label>
                            <Input
                                id="username"
                                type="text"
                                placeholder="Enter your username"
                                value={username}
                                onChange={(e) => setUsername(e.target.value)}
                                required
                                className="bg-black/20 border-white/10 focus:border-primary/50 text-white placeholder:text-gray-500"
                            />
                        </div>
                        <div className="space-y-2">
                            <label className="text-sm font-medium text-gray-300 ml-1">Password</label>
                            <Input
                                id="password"
                                type="password"
                                placeholder="Enter your password"
                                value={password}
                                onChange={(e) => setPassword(e.target.value)}
                                required
                                className="bg-black/20 border-white/10 focus:border-primary/50 text-white placeholder:text-gray-500"
                            />
                        </div>
                    </div>

                    {error && (
                        <div className="text-red-400 text-sm text-center bg-red-500/10 p-3 rounded-lg border border-red-500/20">
                            {error}
                        </div>
                    )}

                    <Button
                        type="submit"
                        className="w-full bg-brand-gradient hover:opacity-90 transition-opacity py-6 text-lg font-bold shadow-lg shadow-primary/20"
                        isLoading={isLoading}
                    >
                        Sign In
                    </Button>

                    <div className="text-center pt-4 border-t border-white/5">
                        <span className="text-gray-400 text-sm">Don't have an account? </span>
                        <Link to="/signup" className="text-primary hover:text-primary-light font-medium text-sm transition-colors">
                            Create Account
                        </Link>
                    </div>
                </form>
            </div>
        </div>
    );
}
