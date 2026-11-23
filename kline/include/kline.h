/* kline: the K+DCAN cable layer (FTDI FT232R, K-line) for macOS and Linux. GPL-3.0.
 *
 * Every int return is a kline_reason. A null handle gives KLINE_IO; nothing crashes on it.
 * One handle per open cable (or replayed recording). kline_cancel may be called from any
 * thread; every other call on a handle must come from one thread at a time. */
#ifndef KLINE_H
#define KLINE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct kline kline_t;

enum kline_reason {
    KLINE_OK = 0,
    KLINE_NO_DEVICE = 1,       /* no such device, or no such recording */
    KLINE_LOCKED = 2,          /* another process holds the cable */
    KLINE_NOT_FTDI = 3,        /* not an FTDI FT232R (0403:6001) */
    KLINE_PERMISSION = 4,
    KLINE_GATE_REFUSED = 5,    /* the frame gate refused a send; latched until close */
    KLINE_IO = 6,
    KLINE_TIMEOUT = 7,
    KLINE_CANCELLED = 8,
    KLINE_REPLAY_MISMATCH = 9, /* replay: the send differs from the recording, or it ran out */
    KLINE_UNSUPPORTED = 10
};

/* Opens the cable with its lock, exclusive access and 1 ms latency. record_dir may be NULL;
 * otherwise every event goes to <record_dir>/kline-<UTC time>-<pid>.jsonl. */
int  kline_open(const char *path, const char *record_dir, kline_t **out);
/* kline_open with the latency timer chosen: latency_ms must be 1 or 16, otherwise
 * KLINE_UNSUPPORTED and nothing is opened. kline_open means latency 1. */
int  kline_open_ex(const char *path, const char *record_dir, unsigned latency_ms, kline_t **out);
/* The latency the open applied: "1ms", "16ms" or "unsupported"; "" for a replay or NULL. */
const char *kline_latency(const kline_t *k);
/* Opens a recording in place of the cable. */
int  kline_open_replay(const char *recording_path, kline_t **out);
void kline_close(kline_t *k);
int  kline_set_line(kline_t *k, uint32_t baud, uint8_t data_bits, uint8_t parity /* 0 none, 1 even, 2 odd */);
int  kline_set_dtr(kline_t *k, int on);
int  kline_set_rts(kline_t *k, int on);
int  kline_get_dsr(kline_t *k, int *dsr);
int  kline_set_break(kline_t *k, int on);           /* held until cleared; the caller times it */
/* The SGBD's communication parameters; params[0] is the concept. Sends fail closed until set. */
int  kline_set_concept(kline_t *k, const uint32_t *params, size_t count);
int  kline_set_interbyte(kline_t *k, uint32_t ms);
int  kline_send(kline_t *k, const uint8_t *data, size_t len);
int  kline_receive(kline_t *k, uint8_t *buf, size_t len, uint32_t timeout_ms, uint32_t tel_end_ms);
int  kline_purge(kline_t *k);
void kline_cancel(kline_t *k, int on);              /* thread-safe */
int  kline_last_error(kline_t *k);                  /* sticky KLINE_GATE_REFUSED once latched */
/* Count of FTDI 0403:6001 devices; path is filled when exactly one. Opens nothing. */
int  kline_find_cable(char *path, size_t path_len);
const char *kline_version(void);

#ifdef __cplusplus
}
#endif

#endif
