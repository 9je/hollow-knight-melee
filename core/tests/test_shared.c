/* test_shared: libmelee_core.so through dlopen, called from several threads (one at a time), one of
 * them with a small stack: guest code must never run on the caller's stack. usage: test_shared <so> <mem1> <iso> */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <pthread.h>
#include <signal.h>
#include <time.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "melee_core.h"

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); exit(1); } } while (0)
static int (*p_init)(const char *, const char *);
static int (*p_step)(void);
static int (*p_start)(const uint8_t *, uint32_t, uint32_t);
static void (*p_solo)(uint8_t *);
static int (*p_read)(int, int, McFighter *);
static void (*p_input)(int, const McInput *);
static void (*p_shutdown)(void);
static const char *g_mem1, *g_iso;

/* A stop-the-world GC (Boehm/Mono) suspends a thread with a signal and scans from the handler's stack
 * pointer to the thread's stack base. Ping the stepping thread with signals and check every handler ran
 * on that thread's own stack, never on the guest's coroutine stack. */
static char *stack_lo, *stack_hi;
static volatile sig_atomic_t pings, outside, stepping;
static void on_ping(int sig) {
  (void)sig;
  volatile char here;
  if ((char *)&here < stack_lo || (char *)&here > stack_hi) outside = 1;
  pings++;
}
static void *pinger(void *target) {
  struct timespec ms = {0, 200000};
  while (stepping) { pthread_kill(*(pthread_t *)target, SIGUSR1); nanosleep(&ms, NULL); }
  return NULL;
}
static void *steps_thread(void *n) {
  pthread_attr_t a; void *base; size_t size;
  pthread_getattr_np(pthread_self(), &a);
  pthread_attr_getstack(&a, &base, &size);
  stack_lo = base; stack_hi = (char *)base + size;
  pthread_t self = pthread_self(), ping;
  stepping = 1;
  CHECK(pthread_create(&ping, NULL, pinger, &self) == 0);
  for (long i = 0; i < (long)(intptr_t)n; i++) CHECK(p_step() == 0);
  stepping = 0;
  pthread_join(ping, NULL);
  return NULL;
}
static void run_on_thread(size_t stack, long n) {
  pthread_attr_t a; pthread_t t;
  pthread_attr_init(&a);
  pthread_attr_setstacksize(&a, stack);
  CHECK(pthread_create(&t, &a, steps_thread, (void *)(intptr_t)n) == 0);
  pthread_join(t, NULL);
}

int main(int argc, char **argv) {
  if (argc < 4) { fprintf(stderr, "usage: test_shared <so> <mem1> <iso>\n"); return 2; }
  g_mem1 = argv[2]; g_iso = argv[3];
  void *h = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
  CHECK(h);
  CHECK(dlsym(h, "guest_lookup") == NULL && dlsym(h, "g_m") == NULL);   /* only mc_* is exported */
  *(void **)&p_init = dlsym(h, "mc_init"); *(void **)&p_step = dlsym(h, "mc_step");
  *(void **)&p_start = dlsym(h, "mc_start_match"); *(void **)&p_solo = dlsym(h, "mc_solo_fox_start");
  *(void **)&p_read = dlsym(h, "mc_read_fighter"); *(void **)&p_input = dlsym(h, "mc_set_input");
  *(void **)&p_shutdown = dlsym(h, "mc_shutdown");
  CHECK(p_init && p_step && p_start && p_solo && p_read && p_input && p_shutdown);
  CHECK(p_init(g_mem1, g_iso) == 0);
  uint8_t start[MC_START_SIZE];
  p_solo(start);
  CHECK(p_start(start, 1, 0) == 0);
  McInput idle = {0};
  p_input(0, &idle);
  for (int p = 1; p < 4; p++) p_input(p, NULL);
  struct sigaction sa = {0};
  sa.sa_handler = on_ping;
  sa.sa_flags = SA_RESTART;
  sigaction(SIGUSR1, &sa, NULL);
  run_on_thread(1u << 20, 90);        /* another thread */
  CHECK(pings > 50 && !outside);      /* every signal found the caller on its own stack */
  run_on_thread(128u << 10, 90);      /* a 128 KiB stack: recompiled guest frames would overflow it */
  McFighter f;
  CHECK(p_read(0, 0, &f) == 0 && f.action == 14 && f.airborne == 0);   /* Wait, on FD */
  p_shutdown();
  CHECK(p_init(g_mem1, g_iso) == 0);  /* the worker restarts after shutdown */
  p_shutdown();
  dlclose(h);
  puts("shared tests passed");
  return 0;
}
