/* The generator's intrin.h: the MSVC intrinsics headers call, declared so their inline helpers parse. */
#ifndef MAGIC_SYSROOT_INTRIN_H
#define MAGIC_SYSROOT_INTRIN_H
void __debugbreak(void);
unsigned short _byteswap_ushort(unsigned short value);
unsigned long _byteswap_ulong(unsigned long value);
unsigned long long _byteswap_uint64(unsigned long long value);
void _ReadWriteBarrier(void);
#endif
