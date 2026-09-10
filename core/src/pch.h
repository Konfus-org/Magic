/**
 * @file pch.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Precompiled prelude for the minion library's own translation units.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once

/*
 * The intersection of two sets: headers nearly every translation unit pulls in
 * anyway, and headers that almost never change. Minion's own public headers
 * deliberately stay out -- a PCH is rebuilt in full, along with every object
 * using it, whenever anything it includes is touched.
 *
 * Under src/ and attached PRIVATE because it is a build-time device, not API.
 *
 * It does not excuse a source file from including what it uses: CMake injects
 * this ahead of the first line, so a .c calling free() without <stdlib.h>
 * compiles until -DMINION_PCH=OFF.
 */

#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#include <math.h>

#include "magic/memory/alloc.h"
#include "magic/memory/box.h"
#include "magic/primitives.h"
#include "magic/random/id.h"
#include "magic/debug/logging.h"
#include "magic/debug/asserts.h"
