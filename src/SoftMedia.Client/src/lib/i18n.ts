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
            "explain.reason.bitrate.clamped": "Bitrate limited to {{kbps}} kbps by {{source}}.",
            "explain.reason.bitrate.user-cap": "Bitrate limited to {{kbps}} kbps by your account's streaming limit.",
            "explain.reason.bitrate.user-remote-cap": "Bitrate limited to {{kbps}} kbps by your account's remote streaming limit.",
            "explain.reason.bitrate.lan-cap": "Bitrate limited to {{kbps}} kbps by the server's home-network limit.",
            "explain.reason.bitrate.wan-cap": "Bitrate limited to {{kbps}} kbps by the server's remote streaming limit.",
            "explain.reason.bitrate.data-saver": "Data Saver is limiting quality on this device (max {{kbps}} kbps).",
            "explain.reason.bitrate.cap-forces-transcode": "The original file's bitrate exceeds the {{capKbps}} kbps limit, so it is converted instead of played directly.",
            "explain.reason.quality.session-override": "Playing at {{quality}} because of your quality selection.",
            "explain.reason.resolution.user-ceiling": "Resolution limited to {{max}} by your account's streaming limit.",
            "explain.reason.resolution.remote-ceiling": "Resolution limited to {{max}} by the server's remote streaming limit.",
            "explain.reason.resolution.server-ceiling": "Resolution limited to {{max}} by this server's conversion limit.",
            "explain.reason.source.is-smaller": "You asked for {{requested}}, but this file is {{source}} — nothing is being limited.",
            "explain.reason.container.unsupported": "The {{container}} file format can't be streamed to this device as-is, so it is being converted.",
            "explain.reason.subtitle.burn-in": "The selected subtitles are being drawn into the picture (burned in) by this conversion.",
            "explain.reason.hdr.tonemap.subtitles": "HDR is converted to SDR because burned-in subtitles must be drawn on converted frames.",
            "explain.reason.hdr.tonemap.server-policy": "HDR is converted to SDR because this server is set not to pass HDR through.",
            "explain.reason.hdr.tonemap.codec": "HDR is converted to SDR because the output format ({{codec}}) can't carry HDR.",
            "hdrguard.title": "HDR will be converted",
            "hdrguard.quality": "This video is HDR, but this playback will convert it to SDR. Colors may look washed out — converted HDR never looks as good as a native SDR copy.",
            "hdrguard.load.noHwAccel": "No hardware acceleration is configured on the server, so this conversion is very CPU-intensive. A server admin can enable it under Settings → Transcoding.",
            "hdrguard.load.partial": "HDR conversion on this server runs partly on the CPU and may be demanding.",
            "hdrguard.blocked": "This server doesn't allow HDR-to-SDR converted playback.",
            "hdrguard.playAnyway": "Play anyway",
            "hdrguard.playVersion": "Play the {{label}} version",
            "hdrguard.neverShow": "Never show this again on this device",
            "hdrguard.cancel": "Cancel"
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
            "explain.reason.bitrate.clamped": "Tasa de bits limitada a {{kbps}} kbps por {{source}}.",
            "explain.reason.bitrate.user-cap": "Tasa de bits limitada a {{kbps}} kbps por el límite de transmisión de tu cuenta.",
            "explain.reason.bitrate.user-remote-cap": "Tasa de bits limitada a {{kbps}} kbps por el límite remoto de transmisión de tu cuenta.",
            "explain.reason.bitrate.lan-cap": "Tasa de bits limitada a {{kbps}} kbps por el límite de red local del servidor.",
            "explain.reason.bitrate.wan-cap": "Tasa de bits limitada a {{kbps}} kbps por el límite de transmisión remota del servidor.",
            "explain.reason.bitrate.data-saver": "El ahorro de datos está limitando la calidad en este dispositivo (máx. {{kbps}} kbps).",
            "explain.reason.bitrate.cap-forces-transcode": "La tasa de bits del archivo original supera el límite de {{capKbps}} kbps, así que se convierte en lugar de reproducirse directamente.",
            "explain.reason.quality.session-override": "Reproduciendo a {{quality}} por tu selección de calidad.",
            "explain.reason.resolution.user-ceiling": "Resolución limitada a {{max}} por el límite de transmisión de tu cuenta.",
            "explain.reason.resolution.remote-ceiling": "Resolución limitada a {{max}} por el límite de transmisión remota del servidor.",
            "explain.reason.resolution.server-ceiling": "Resolución limitada a {{max}} por el límite de conversión de este servidor.",
            "explain.reason.source.is-smaller": "Pediste {{requested}}, pero este archivo es {{source}} — nada está limitando la calidad.",
            "explain.reason.container.unsupported": "El formato de archivo {{container}} no se puede transmitir tal cual a este dispositivo, así que se está convirtiendo.",
            "explain.reason.subtitle.burn-in": "Los subtítulos seleccionados se están dibujando sobre la imagen (incrustados) en esta conversión.",
            "explain.reason.hdr.tonemap.subtitles": "El HDR se convierte a SDR porque los subtítulos incrustados deben dibujarse sobre fotogramas convertidos.",
            "explain.reason.hdr.tonemap.server-policy": "El HDR se convierte a SDR porque este servidor está configurado para no dejar pasar el HDR.",
            "explain.reason.hdr.tonemap.codec": "El HDR se convierte a SDR porque el formato de salida ({{codec}}) no puede transportar HDR.",
            "hdrguard.title": "El HDR se convertirá",
            "hdrguard.quality": "Este vídeo es HDR, pero esta reproducción lo convertirá a SDR. Los colores pueden verse lavados — el HDR convertido nunca se ve tan bien como una copia SDR nativa.",
            "hdrguard.load.noHwAccel": "El servidor no tiene aceleración por hardware configurada, así que esta conversión consume mucha CPU. Un administrador puede activarla en Ajustes → Transcodificación.",
            "hdrguard.load.partial": "La conversión de HDR en este servidor se ejecuta en parte en la CPU y puede ser exigente.",
            "hdrguard.blocked": "Este servidor no permite la reproducción de HDR convertido a SDR.",
            "hdrguard.playAnyway": "Reproducir de todos modos",
            "hdrguard.playVersion": "Reproducir la versión {{label}}",
            "hdrguard.neverShow": "No volver a mostrar en este dispositivo",
            "hdrguard.cancel": "Cancelar"
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
