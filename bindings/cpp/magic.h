/**
 * @file magic.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The whole public API behind a single include, ready for C++.
 * @copyright Magic Game Engine is Copyright (c) Jeremy Hummel
 *
 * GENERATED FILE -- DO NOT EDIT. Written by cmake/MagicUmbrella.cmake from the
 * glob over core/include, using the template at cmake/magic.h.in. A header
 * added under core/include appears here on the next configure; edits made here
 * are overwritten by it.
 *
 * A C++ consumer includes this and nothing else. Magic is a C library, so its
 * declarations need C linkage to be callable from C++ -- and that linkage is
 * applied here, once, rather than by a guard repeated in every header and
 * forgotten in one of them. Including an individual header directly from C++
 * declares its functions with C++ linkage, and the mangled symbols that
 * produces are not the ones the library exports: it compiles, and then fails at
 * link time. From C it makes no difference; include whichever headers you use.
 *
 * This is a convenience for consumers and not how the library includes its own
 * headers: a translation unit inside Magic includes what it uses, so touching
 * one header does not rebuild every object that only needed another.
 */

#pragma once

/* The standard headers Magic's public headers reach for, pulled in ahead of
 * the C linkage block: a standard header expanded inside extern "C" is
 * ill-formed. Harvested from the headers at configure time, not by hand. */
#include <stdlib.h>

#ifdef __cplusplus
extern "C" {
#endif

#include "magic/collections/array.h"
#include "magic/collections/buffer.h"
#include "magic/collections/iter.h"
#include "magic/collections/list.h"
#include "magic/collections/string.h"
#include "magic/collections/view.h"
#include "magic/commands/builtin.h"
#include "magic/commands/parser.h"
#include "magic/commands/registry.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/events/event.h"
#include "magic/events/registry.h"
#include "magic/export.h"
#include "magic/files/dirs.h"
#include "magic/files/file.h"
#include "magic/files/parsing.h"
#include "magic/gems/gem.h"
#include "magic/gems/loading.h"
#include "magic/gems/slots.h"
#include "magic/gems/state.h"
#include "magic/memory/alloc.h"
#include "magic/memory/allocator.h"
#include "magic/memory/box.h"
#include "magic/memory/copy.h"
#include "magic/memory/slots.h"
#include "magic/memory/stats.h"
#include "magic/platform/defines.h"
#include "magic/platform/input.h"
#include "magic/platform/input_codes.h"
#include "magic/platform/shared_lib.h"
#include "magic/platform/slots.h"
#include "magic/platform/state.h"
#include "magic/primitives.h"
#include "magic/random/generators.h"
#include "magic/random/id.h"
#include "magic/time/datetime.h"
#include "magic/time/delta_time.h"
#include "magic/time/frame.h"
#include "magic/time/timestamp.h"

#ifdef __cplusplus
}
#endif
