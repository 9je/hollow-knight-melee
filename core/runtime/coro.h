/* coro.h - minimal asymmetric coroutines: the guest game runs on its own stack and yields at frame boundaries. */
#pragma once
#include <stddef.h>
typedef struct Coro Coro;
Coro *coro_new(void (*fn)(void *), void *arg, size_t stack);
int coro_resume(Coro *co);   /* run until it yields (returns 1) or finishes (returns 0) */
void coro_yield(void);        /* called on a coroutine: switch back to the resumer */
void coro_free(Coro *co);
