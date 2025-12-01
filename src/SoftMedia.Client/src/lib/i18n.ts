import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import LanguageDetector from 'i18next-browser-languagedetector';

// Basic translation resources
// In a real app, these would be in separate JSON files
const resources = {
    en: {
        translation: {
            "Home": "Home",
            "Libraries": "Libraries",
            "Settings": "Settings",
            "Sign Out": "Sign Out",
            "Server": "Server",
            "Network": "Network",
            "Playback": "Playback",
            "Metadata": "Metadata",
            "Users": "Users",
            "Language": "Language",
            "Log Level": "Log Level",
            "Save Changes": "Save Changes",
            "Settings saved successfully": "Settings saved successfully",
            "Failed to save settings": "Failed to save settings"
        }
    },
    es: {
        translation: {
            "Home": "Inicio",
            "Libraries": "Bibliotecas",
            "Settings": "Ajustes",
            "Sign Out": "Cerrar Sesión",
            "Server": "Servidor",
            "Network": "Red",
            "Playback": "Reproducción",
            "Metadata": "Metadatos",
            "Users": "Usuarios",
            "Language": "Idioma",
            "Log Level": "Nivel de Registro",
            "Save Changes": "Guardar Cambios",
            "Settings saved successfully": "Ajustes guardados correctamente",
            "Failed to save settings": "Error al guardar los ajustes"
        }
    }
};

i18n
    .use(LanguageDetector)
    .use(initReactI18next)
    .init({
        resources,
        fallbackLng: 'en',
        interpolation: {
            escapeValue: false // react already safes from xss
        },
        detection: {
            order: ['localStorage', 'navigator'],
            caches: ['localStorage']
        }
    });

export default i18n;
