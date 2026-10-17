/* The generator's stddef.h: the standard types from clang's builtin macros, so a parse depends on no installed
   libc and reads the same on every machine. */
#ifndef MAGIC_SYSROOT_STDDEF_H
#define MAGIC_SYSROOT_STDDEF_H
typedef __SIZE_TYPE__ size_t;
typedef __PTRDIFF_TYPE__ ptrdiff_t;
#ifndef __cplusplus
typedef __WCHAR_TYPE__ wchar_t;
#endif
typedef struct { long long __ll; long double __ld; } max_align_t;
#define NULL ((void*)0)
#define offsetof(type, member) __builtin_offsetof(type, member)
#endif
