/* worker.c - one core-owned thread runs every coroutine resume, so guest code never runs on (or switches
 * away from) a caller's stack. Hosts with a stack-scanning GC (Mono in Hollow Knight) call mc_* from
 * managed threads; this keeps the guest's coroutine stack invisible to them. */
#include <pthread.h>
#include "mc_internal.h"

static pthread_t thread;
static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t cv = PTHREAD_COND_INITIALIZER;
static int running, has_job, quit;
static void (*job_fn)(void *);
static void *job_arg;

static void *loop(void *unused) {
  (void)unused;
  pthread_mutex_lock(&mu);
  for (;;) {
    while (!has_job && !quit) pthread_cond_wait(&cv, &mu);
    if (quit) break;
    pthread_mutex_unlock(&mu);
    job_fn(job_arg);
    pthread_mutex_lock(&mu);
    has_job = 0;
    pthread_cond_broadcast(&cv);
  }
  pthread_mutex_unlock(&mu);
  return NULL;
}

void worker_call(void (*fn)(void *), void *arg) {
  pthread_mutex_lock(&mu);
  if (!running) {
    pthread_attr_t a;
    pthread_attr_init(&a);
    pthread_attr_setstacksize(&a, 1u << 20);
    quit = 0;
    if (pthread_create(&thread, &a, loop, NULL) != 0) { pthread_mutex_unlock(&mu); fn(arg); return; }
    running = 1;
  }
  job_fn = fn; job_arg = arg; has_job = 1;
  pthread_cond_broadcast(&cv);
  while (has_job) pthread_cond_wait(&cv, &mu);
  pthread_mutex_unlock(&mu);
}

void worker_stop(void) {
  pthread_mutex_lock(&mu);
  if (!running) { pthread_mutex_unlock(&mu); return; }
  quit = 1;
  pthread_cond_broadcast(&cv);
  pthread_mutex_unlock(&mu);
  pthread_join(thread, NULL);
  running = 0;
}
