import axios from 'axios';
import { useAuthStore } from '../store/authStore';

export const API_URL = '/api/v1';

const api = axios.create({
    baseURL: API_URL,
    headers: {
        'Content-Type': 'application/json',
    },
    withCredentials: true, // Important for HttpOnly cookies
});

// Request Interceptor: Attach Token
api.interceptors.request.use(
    (config) => {
        const token = useAuthStore.getState().token;
        if (token) {
            config.headers.Authorization = `Bearer ${token}`;
        }
        return config;
    },
    (error) => Promise.reject(error)
);

// Response Interceptor: Handle 401 & Refresh
api.interceptors.response.use(
    (response) => response,
    async (error) => {
        const originalRequest = error.config;

        // If 401 and not already retrying
        if (error.response?.status === 401 && !originalRequest._retry) {
            originalRequest._retry = true;

            try {
                // Attempt to refresh token
                // Note: The backend should look for the refresh token in the HttpOnly cookie
                const response = await axios.post('/api/v1/auth/refresh-token', {}, {
                    withCredentials: true
                });

                const { token, user } = response.data;

                // Update store
                useAuthStore.getState().login(user, token);

                // Retry original request with new token
                originalRequest.headers.Authorization = `Bearer ${token}`;
                return api(originalRequest);
            } catch (refreshError) {
                // Refresh failed - logout user
                useAuthStore.getState().logout();
                return Promise.reject(refreshError);
            }
        }

        return Promise.reject(error);
    }
);

export default api;
