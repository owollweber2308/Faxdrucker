/*
 * faxshim - schlanke, versionsstabile C-API über spandsp für FaxDrucker.
 *
 * Die C#-Seite spricht nur diese Funktionen an. Unterschiede zwischen
 * spandsp 0.0.6 und 3.x (Callback-Signaturen, Logging) werden hier versteckt.
 */
#ifndef FAXSHIM_H
#define FAXSHIM_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(FAXSHIM_BUILD)
#    define FS_API __declspec(dllexport)
#  else
#    define FS_API __declspec(dllimport)
#  endif
#  define FS_CALL __cdecl
#else
#  define FS_API __attribute__((visibility("default")))
#  define FS_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct fs_session fs_session_t;

/* Wird für jedes ausgehende T.38-IFP-Paket aufgerufen. count = gewünschte Anzahl Wiederholungen. */
typedef int (FS_CALL *fs_t38_tx_cb)(void *user_data, const uint8_t *buf, int len, int count);
/* Log-Ausgabe von spandsp (Ebene + Text). */
typedef void (FS_CALL *fs_log_cb)(void *user_data, int level, const char *text);

enum
{
    FS_MODE_AUDIO = 0,   /* Fax über G.711 (Audio-Modems in spandsp) */
    FS_MODE_T38 = 1      /* Fax über T.38 (IFP-Pakete) */
};

/*
 * Neue Sitzung. calling = 1 für den Sender (wir rufen an).
 * tx_tiff = zu sendende TIFF-Datei (NULL beim Empfangen),
 * rx_tiff = Zieldatei beim Empfangen (nur für Tests, sonst NULL).
 */
FS_API fs_session_t * FS_CALL fs_new(int mode, int calling,
                                     const char *tx_tiff, const char *rx_tiff,
                                     const char *ident, const char *header,
                                     int ecm);
FS_API void FS_CALL fs_free(fs_session_t *s);

FS_API void FS_CALL fs_set_log(fs_session_t *s, fs_log_cb cb, void *user_data, int verbose);

/* T.38 */
FS_API void FS_CALL fs_t38_set_tx(fs_session_t *s, fs_t38_tx_cb cb, void *user_data);
FS_API int FS_CALL fs_t38_rx(fs_session_t *s, const uint8_t *buf, int len, uint16_t seq_no);
/* Zeit voranschreiten lassen (8000 Samples = 1 s). Gibt 1 zurück, wenn die Sitzung beendet ist. */
FS_API int FS_CALL fs_t38_tick(fs_session_t *s, int samples);

/* G.711 / Audio: 16-Bit-PCM, 8 kHz */
FS_API int FS_CALL fs_audio_rx(fs_session_t *s, int16_t *amp, int len);
FS_API int FS_CALL fs_audio_tx(fs_session_t *s, int16_t *amp, int max_len);

/* Status */
FS_API int FS_CALL fs_is_done(fs_session_t *s);
FS_API int FS_CALL fs_result(fs_session_t *s);              /* T.30-Abschlusscode, -1 solange offen */
FS_API const char * FS_CALL fs_result_text(int code);
FS_API int FS_CALL fs_pages_tx(fs_session_t *s);
FS_API int FS_CALL fs_pages_rx(fs_session_t *s);
FS_API int FS_CALL fs_bit_rate(fs_session_t *s);
FS_API const char * FS_CALL fs_remote_ident(fs_session_t *s);
FS_API void FS_CALL fs_terminate(fs_session_t *s);

/* G.711-Hilfsfunktionen */
FS_API void FS_CALL fs_alaw_encode(const int16_t *amp, uint8_t *out, int len);
FS_API void FS_CALL fs_alaw_decode(const uint8_t *in, int16_t *amp, int len);
FS_API void FS_CALL fs_ulaw_encode(const int16_t *amp, uint8_t *out, int len);
FS_API void FS_CALL fs_ulaw_decode(const uint8_t *in, int16_t *amp, int len);

FS_API const char * FS_CALL fs_spandsp_version(void);
/* T.30-Code "Gegenstelle unterstützt die Auflösung nicht" (Wert hängt von der spandsp-Version ab). */
FS_API int FS_CALL fs_code_no_resolution_support(void);

#ifdef __cplusplus
}
#endif

#endif
