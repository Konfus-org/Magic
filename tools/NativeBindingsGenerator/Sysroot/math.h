/* The generator's math.h: a C header may include it for its functions, none of which a declaration needs. */
#ifndef MAGIC_SYSROOT_MATH_H
#define MAGIC_SYSROOT_MATH_H
#define HUGE_VAL (__builtin_huge_val())
#define INFINITY (__builtin_inff())
#define NAN (__builtin_nanf(""))
double sqrt(double);
double fabs(double);
double floor(double);
double ceil(double);
double pow(double, double);
double sin(double);
double cos(double);
double tan(double);
double atan2(double, double);
float sqrtf(float);
float fabsf(float);
#endif
