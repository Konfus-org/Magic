/* The generator's process.h: the MSVC thread entry points a header may name. */
#ifndef MAGIC_SYSROOT_PROCESS_H
#define MAGIC_SYSROOT_PROCESS_H
#include <stdint.h>
uintptr_t _beginthreadex(void* security, unsigned stackSize, unsigned (__stdcall* start)(void*), void* argument, unsigned flags, unsigned* threadId);
void _endthreadex(unsigned code);
#endif
