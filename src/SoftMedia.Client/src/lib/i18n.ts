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
            "Failed to save settings": "Failed to save settings",
            "Close": "Close",
            "explain.title": "Why is this playing this way?",
            "explain.method.directplay": "Playing directly — no conversion needed.",
            "explain.method.remux": "Repackaging the stream (no quality loss).",
            "explain.method.transcode": "Converting the stream for your device.",
            "explain.directplay.detail": "Your device supports this file's format and codecs natively, so it streams as-is.",
            "explain.transcode.generic": "This stream is being converted for compatibility.",
            "explain.reason.directplay.supported": "Format and codecs are natively supported by your device.",
            "explain.reason.remux.container": "Codecs are supported, but the {{container}} container is repackaged to HLS.",
            "explain.reason.video.codec.unsupported": "Your device can't play the {{codec}} video codec — converting to a supported one.",
            "explain.reason.audio.codec.unsupported": "Your device can't play the {{codec}} audio codec — converting to a supported one.",
            "explain.reason.hdr.tonemap": "HDR is being tone-mapped to SDR for your display.",
            "explain.reason.resolution.exceeds-max": "Source resolution {{resolution}} exceeds your limit {{max}} — downscaling.",
            "explain.reason.transcode.required": "This stream requires conversion for playback.",
            "explain.reason.bitrate.clamped": "Bitrate limited to {{kbps}} kbps by {{source}}."
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
            "Failed to save settings": "Error al guardar los ajustes",
            "Close": "Cerrar",
            "explain.title": "¿Por qué se reproduce así?",
            "explain.method.directplay": "Reproducción directa — sin conversión.",
            "explain.method.remux": "Reempaquetando el flujo (sin pérdida de calidad).",
            "explain.method.transcode": "Convirtiendo el flujo para tu dispositivo.",
            "explain.directplay.detail": "Tu dispositivo admite el formato y los códecs de este archivo de forma nativa, así que se transmite tal cual.",
            "explain.transcode.generic": "Este flujo se está convirtiendo por compatibilidad.",
            "explain.reason.directplay.supported": "El formato y los códecs son compatibles de forma nativa con tu dispositivo.",
            "explain.reason.remux.container": "Los códecs son compatibles, pero el contenedor {{container}} se reempaqueta a HLS.",
            "explain.reason.video.codec.unsupported": "Tu dispositivo no puede reproducir el códec de vídeo {{codec}} — convirtiéndolo a uno compatible.",
            "explain.reason.audio.codec.unsupported": "Tu dispositivo no puede reproducir el códec de audio {{codec}} — convirtiéndolo a uno compatible.",
            "explain.reason.hdr.tonemap": "El HDR se está convirtiendo a SDR para tu pantalla.",
            "explain.reason.resolution.exceeds-max": "La resolución de origen {{resolution}} supera tu límite {{max}} — reduciéndola.",
            "explain.reason.transcode.required": "Este flujo requiere conversión para reproducirse.",
            "explain.reason.bitrate.clamped": "Tasa de bits limitada a {{kbps}} kbps por {{source}}."
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
