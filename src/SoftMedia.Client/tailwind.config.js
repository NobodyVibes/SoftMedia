/** @type {import('tailwindcss').Config} */
export default {
    content: [
        "./index.html",
        "./src/**/*.{js,ts,jsx,tsx}",
    ],
    theme: {
        extend: {
            colors: {
                // SDD: "Bright Blue (#007AFF) to Violet (#8A2BE2)"
                primary: {
                    DEFAULT: '#007AFF',
                    dark: '#005BB5',
                },
                secondary: {
                    DEFAULT: '#8A2BE2',
                    dark: '#4B0082',
                },
                background: '#0f172a', // Slate-900 for dark mode base
                surface: '#1e293b',    // Slate-800 for cards
            },
            backgroundImage: {
                'brand-gradient': 'linear-gradient(to right, #007AFF, #8A2BE2)',
            }
        },
    },
    plugins: [],
}
