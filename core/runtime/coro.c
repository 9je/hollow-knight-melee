/* coro.c - coroutines on ucontext (Linux). A Windows port uses Fibers with the same API. */
#define _XOPEN_SOURCE 700
#include <stdlib.h>
#include <ucontext.h>
#include "coro.h"

struct Coro { ucontext_t ctx, caller; void (*fn)(void *); void *arg; void *stack; int done; };
static Coro *cur;

static void trampoline(void) {
  Coro *co = cur;
  co->fn(co->arg);
  co->done = 1;
  swapcontext(&co->ctx, &co->caller);
}
Coro *coro_new(void (*fn)(void *), void *arg, size_t stack) {
  Coro *co = calloc(1, sizeof *co);
  if (!co || !(co->stack = malloc(stack))) { free(co); return NULL; }
  co->fn = fn;
  co->arg = arg;
  getcontext(&co->ctx);
  co->ctx.uc_stack.ss_sp = co->stack;
  co->ctx.uc_stack.ss_size = stack;
  co->ctx.uc_link = NULL;
  makecontext(&co->ctx, trampoline, 0);
  return co;
}
int coro_resume(Coro *co) {
  if (co->done) return 0;
  Coro *prev = cur;
  cur = co;
  swapcontext(&co->caller, &co->ctx);
  cur = prev;
  return !co->done;
}
void coro_yield(void) { swapcontext(&cur->ctx, &cur->caller); }
void coro_free(Coro *co) { if (co) { free(co->stack); free(co); } }
