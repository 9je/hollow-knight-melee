/* coro.c - coroutines: Fibers on Windows, ucontext elsewhere (Linux). */
#ifdef _WIN32
#include <stdlib.h>
#include <windows.h>
#include "coro.h"

struct Coro { void *fiber, *caller; void (*fn)(void *); void *arg; int done; };
static Coro *cur;

static void CALLBACK entry(void *p) {
  Coro *co = p;
  co->fn(co->arg);
  co->done = 1;
  SwitchToFiber(co->caller);
}
Coro *coro_new(void (*fn)(void *), void *arg, size_t stack) {
  Coro *co = calloc(1, sizeof *co);
  if (!co) return NULL;
  co->fn = fn;
  co->arg = arg;
  if (!(co->fiber = CreateFiber(stack, entry, co))) { free(co); return NULL; }
  return co;
}
int coro_resume(Coro *co) {
  if (co->done) return 0;
  /* the resuming thread becomes a fiber the first time (worker.c's thread, which every resume runs on) */
  void *self = IsThreadAFiber() ? GetCurrentFiber() : ConvertThreadToFiber(NULL);
  if (!self) return 0;
  Coro *prev = cur;
  cur = co;
  co->caller = self;
  SwitchToFiber(co->fiber);
  cur = prev;
  return !co->done;
}
void coro_yield(void) { SwitchToFiber(cur->caller); }
void coro_free(Coro *co) { if (co) { if (co->fiber) DeleteFiber(co->fiber); free(co); } }
#else
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
#endif
