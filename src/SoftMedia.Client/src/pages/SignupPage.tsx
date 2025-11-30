import { useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import api from '../services/api';
import { Input } from '../components/ui/Input';
import { Button } from '../components/ui/Button';

export default function SignupPage() {
    const navigate = useNavigate();

    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [confirmPassword, setConfirmPassword] = useState('');
    const [error, setError] = useState('');
    const [isLoading, setIsLoading] = useState(false);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError('');

        if (password !== confirmPassword) {
            setError("Passwords don't match");
            return;
        }

        setIsLoading(true);

        try {
            await api.post('/auth/signup', { username, password });
            // On success, redirect to login
            navigate('/login');
        } catch (err: any) {
            console.error(err);
            setError(err.response?.data?.message || 'Failed to create account');
        } finally {
            setIsLoading(false);
        }
    };

    return (
        <div className="min-h-screen flex items-center justify-center bg-background p-4">
            <div className="w-full max-w-md space-y-8 bg-surface p-8 rounded-xl shadow-2xl border border-slate-700">
                <div className="text-center">
                    <h2 className="text-3xl font-bold bg-clip-text text-transparent bg-brand-gradient">
                        Create Account
                    </h2>
                    <p className="mt-2 text-sm text-gray-400">
                        Join SoftMedia to stream your collection
                    </p>
                </div>

                <form className="mt-8 space-y-6" onSubmit={handleSubmit}>
                    <div className="space-y-4">
                        <Input
                            id="username"
                            type="text"
                            label="Username"
                            placeholder="Choose a username"
                            value={username}
                            onChange={(e) => setUsername(e.target.value)}
                            required
                        />
                        <Input
                            id="password"
                            type="password"
                            label="Password"
                            placeholder="Choose a password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            required
                        />
                        <Input
                            id="confirmPassword"
                            type="password"
                            label="Confirm Password"
                            placeholder="Confirm your password"
                            value={confirmPassword}
                            onChange={(e) => setConfirmPassword(e.target.value)}
                            required
                        />
                    </div>

                    {error && (
                        <div className="text-red-500 text-sm text-center bg-red-500/10 p-2 rounded">
                            {error}
                        </div>
                    )}

                    <Button type="submit" className="w-full" isLoading={isLoading}>
                        Sign up
                    </Button>

                    <div className="text-center text-sm">
                        <span className="text-gray-400">Already have an account? </span>
                        <Link to="/login" className="text-primary hover:text-primary-dark font-medium">
                            Sign in
                        </Link>
                    </div>
                </form>
            </div>
        </div>
    );
}
