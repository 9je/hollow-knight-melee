/* standin.h - a second fighter standing in for a Hollow Knight enemy (plan 7): Fox's grabs and Melee's push act on it.
 * It is never drawn or heard, and fighters' attacks and items never hit it, except while it is held (pummels, throws). */
#pragma once
#include <stdint.h>
int mc_standin_set(int port);                          /* 0 ok; -1 clears. Asleep from the next frame boundary */
int mc_standin_wake(float x, float y, float facing);   /* next boundary: at (x, y), Wait on a floor there, else Fall */
int mc_standin_sleep(void);                            /* next boundary: Melee's Sleep (no logic, push or drawing) */
int mc_standin_held(void);
int mc_standin_hold(float frames);   /* the grab's release timer, set while held (frames) */                             /* 1 while in a grab link, 0 not, -1 no stand-in */
