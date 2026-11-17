/*
 * faxshim - Implementierung. Siehe faxshim.h.
 */
#define FAXSHIM_BUILD 1

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <inttypes.h>

#include <tiffio.h>
#include <spandsp.h>
#include <spandsp/version.h>

#include "faxshim.h"

/* spandsp >= 2012 (Freeswitch-Linie, 3.x) hat geänderte Callback-Signaturen. */
#if !defined(SPANDSP_RELEASE_DATE)
#  error "spandsp/version.h fehlt"
#endif
#if SPANDSP_RELEASE_DATE >= 20120101
#  define FS_NEW_API 1
#else
#  define FS_NEW_API 0
#endif

struct fs_session
{
    int mode;
    int calling;
    fax_state_t *fax;
    t38_terminal_state_t *t38;
    t30_state_t *t30;
    volatile int done;
    volatile int result;
    fs_t38_tx_cb t38_tx;
    void *t38_tx_ud;
    fs_log_cb log;
    void *log_ud;
    char version[64];
};

/* ---------- Phase E (Ende der Übertragung) ---------- */

static void on_phase_e(fs_session_t *s, int completion_code)
{
    s->result = completion_code;
    s->done = 1;
}

#if FS_NEW_API
static void phase_e_handler(void *user_data, int completion_code)
{
    on_phase_e((fs_session_t *) user_data, completion_code);
}
#else
static void phase_e_handler(t30_state_t *t, void *user_data, int completion_code)
{
    (void) t;
    on_phase_e((fs_session_t *) user_data, completion_code);
}
#endif

/* ---------- T.38-Sendepfad ---------- */

static int t38_tx_handler(t38_core_state_t *core, void *user_data, const uint8_t *buf, int len, int count)
{
    fs_session_t *s = (fs_session_t *) user_data;
    (void) core;
    if (s->t38_tx)
        return s->t38_tx(s->t38_tx_ud, buf, len, count);
    return 0;
}

/* ---------- Logging ---------- */

#if FS_NEW_API
static void log_handler(void *user_data, int level, const char *text)
{
    fs_session_t *s = (fs_session_t *) user_data;
    if (s && s->log)
        s->log(s->log_ud, level, text);
}
#else
/* 0.0.6 kennt kein user_data im Log-Callback; eine Sitzung pro Thread genügt. */
#  if defined(_MSC_VER)
static __declspec(thread) fs_session_t *log_session = NULL;
#  else
static __thread fs_session_t *log_session = NULL;
#  endif
static void log_handler(int level, const char *text)
{
    fs_session_t *s = log_session;
    if (s && s->log)
        s->log(s->log_ud, level, text);
}
#endif

static void attach_log(fs_session_t *s, logging_state_t *ls, int level)
{
    if (ls == NULL)
        return;
    span_log_set_level(ls, level);
#if FS_NEW_API
    span_log_set_message_handler(ls, log_handler, s);
#else
    span_log_set_message_handler(ls, log_handler);
#endif
}

/* ---------- Lebenszyklus ---------- */

FS_API fs_session_t * FS_CALL fs_new(int mode, int calling,
                                     const char *tx_tiff, const char *rx_tiff,
                                     const char *ident, const char *header,
                                     int ecm)
{
    fs_session_t *s = (fs_session_t *) calloc(1, sizeof(*s));
    if (s == NULL)
        return NULL;
    s->mode = mode;
    s->calling = calling;
    s->result = -1;

    if (mode == FS_MODE_T38)
    {
        s->t38 = t38_terminal_init(NULL, calling ? 1 : 0, t38_tx_handler, s);
        if (s->t38 == NULL)
        {
            free(s);
            return NULL;
        }
        t38_terminal_set_config(s->t38, 0);
        t38_terminal_set_tep_mode(s->t38, 0);
        s->t30 = t38_terminal_get_t30_state(s->t38);
    }
    else
    {
        s->fax = fax_init(NULL, calling ? 1 : 0);
        if (s->fax == NULL)
        {
            free(s);
            return NULL;
        }
        fax_set_transmit_on_idle(s->fax, 1);
        fax_set_tep_mode(s->fax, 0);
        s->t30 = fax_get_t30_state(s->fax);
    }

    if (ident && *ident)
        t30_set_tx_ident(s->t30, ident);
    if (header && *header)
        t30_set_tx_page_header_info(s->t30, header);
    if (tx_tiff && *tx_tiff)
        t30_set_tx_file(s->t30, tx_tiff, -1, -1);
    if (rx_tiff && *rx_tiff)
        t30_set_rx_file(s->t30, rx_tiff, -1);
    t30_set_ecm_capability(s->t30, ecm ? 1 : 0);
    t30_set_phase_e_handler(s->t30, phase_e_handler, s);
    return s;
}

FS_API void FS_CALL fs_free(fs_session_t *s)
{
    if (s == NULL)
        return;
#if !FS_NEW_API
    if (log_session == s)
        log_session = NULL;
#endif
    if (s->t38)
        t38_terminal_free(s->t38);
    if (s->fax)
        fax_free(s->fax);
    free(s);
}

FS_API void FS_CALL fs_set_log(fs_session_t *s, fs_log_cb cb, void *user_data, int verbose)
{
    int level;

    s->log = cb;
    s->log_ud = user_data;
#if !FS_NEW_API
    log_session = s;
#endif
    level = SPAN_LOG_SHOW_SEVERITY | SPAN_LOG_SHOW_PROTOCOL | (verbose ? SPAN_LOG_FLOW : SPAN_LOG_WARNING);
    attach_log(s, t30_get_logging_state(s->t30), level);
    if (s->t38)
    {
        attach_log(s, t38_terminal_get_logging_state(s->t38), level);
        attach_log(s, t38_core_get_logging_state(t38_terminal_get_t38_core_state(s->t38)), level);
    }
    if (s->fax)
        attach_log(s, fax_get_logging_state(s->fax), level);
}

/* ---------- T.38 ---------- */

FS_API void FS_CALL fs_t38_set_tx(fs_session_t *s, fs_t38_tx_cb cb, void *user_data)
{
    s->t38_tx = cb;
    s->t38_tx_ud = user_data;
}

FS_API int FS_CALL fs_t38_rx(fs_session_t *s, const uint8_t *buf, int len, uint16_t seq_no)
{
    if (s->t38 == NULL)
        return -1;
#if !FS_NEW_API
    log_session = s;
#endif
    return t38_core_rx_ifp_packet(t38_terminal_get_t38_core_state(s->t38), buf, len, seq_no);
}

FS_API int FS_CALL fs_t38_tick(fs_session_t *s, int samples)
{
    if (s->t38 == NULL)
        return 1;
#if !FS_NEW_API
    log_session = s;
#endif
    if (t38_terminal_send_timeout(s->t38, samples))
        s->done = 1;
    return s->done;
}

/* ---------- Audio ---------- */

FS_API int FS_CALL fs_audio_rx(fs_session_t *s, int16_t *amp, int len)
{
    if (s->fax == NULL)
        return -1;
#if !FS_NEW_API
    log_session = s;
#endif
    return fax_rx(s->fax, amp, len);
}

FS_API int FS_CALL fs_audio_tx(fs_session_t *s, int16_t *amp, int max_len)
{
    if (s->fax == NULL)
        return -1;
#if !FS_NEW_API
    log_session = s;
#endif
    return fax_tx(s->fax, amp, max_len);
}

/* ---------- Status ---------- */

FS_API int FS_CALL fs_is_done(fs_session_t *s)
{
    return s->done;
}

FS_API int FS_CALL fs_result(fs_session_t *s)
{
    return s->done ? s->result : -1;
}

FS_API const char * FS_CALL fs_result_text(int code)
{
    return t30_completion_code_to_str(code);
}

FS_API int FS_CALL fs_pages_tx(fs_session_t *s)
{
    t30_stats_t st;
    memset(&st, 0, sizeof(st));
    t30_get_transfer_statistics(s->t30, &st);
    return st.pages_tx;
}

FS_API int FS_CALL fs_pages_rx(fs_session_t *s)
{
    t30_stats_t st;
    memset(&st, 0, sizeof(st));
    t30_get_transfer_statistics(s->t30, &st);
    return st.pages_rx;
}

FS_API int FS_CALL fs_bit_rate(fs_session_t *s)
{
    t30_stats_t st;
    memset(&st, 0, sizeof(st));
    t30_get_transfer_statistics(s->t30, &st);
    return st.bit_rate;
}

FS_API const char * FS_CALL fs_remote_ident(fs_session_t *s)
{
    const char *id = t30_get_rx_ident(s->t30);
    return id ? id : "";
}

FS_API void FS_CALL fs_terminate(fs_session_t *s)
{
    t30_terminate(s->t30);
}

/* ---------- G.711 ---------- */

FS_API void FS_CALL fs_alaw_encode(const int16_t *amp, uint8_t *out, int len)
{
    int i;
    for (i = 0; i < len; i++)
        out[i] = linear_to_alaw(amp[i]);
}

FS_API void FS_CALL fs_alaw_decode(const uint8_t *in, int16_t *amp, int len)
{
    int i;
    for (i = 0; i < len; i++)
        amp[i] = alaw_to_linear(in[i]);
}

FS_API void FS_CALL fs_ulaw_encode(const int16_t *amp, uint8_t *out, int len)
{
    int i;
    for (i = 0; i < len; i++)
        out[i] = linear_to_ulaw(amp[i]);
}

FS_API void FS_CALL fs_ulaw_decode(const uint8_t *in, int16_t *amp, int len)
{
    int i;
    for (i = 0; i < len; i++)
        amp[i] = ulaw_to_linear(in[i]);
}

FS_API const char * FS_CALL fs_spandsp_version(void)
{
    return SPANDSP_RELEASE_DATETIME_STRING;
}

FS_API int FS_CALL fs_code_no_resolution_support(void)
{
    return T30_ERR_NORESSUPPORT;
}
