/**
 * @file sdl_gem.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief The SDL platform backend: gem identity, lifecycle, and the platform
 * slots it fills.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/collections/string.h"
#include "magic/debug/asserts.h"
#include "magic/debug/logging.h"
#include "magic/events/event.h"
#include "magic/gems/slots.h"
#include "magic/gems/state.h"
#include "magic/memory/alloc.h"
#include "magic/memory/slots.h"
#include "magic/platform/slots.h"
#include <SDL3/SDL.h>

/** @brief What the window opens as when the initial state does not say. */
#define SDL_GEM_DEFAULT_TITLE "Magic"
#define SDL_GEM_DEFAULT_WIDTH 1280
#define SDL_GEM_DEFAULT_HEIGHT 720

/** @brief The one window this backend drives; NULL until setup succeeds. */
static SDL_Window* sdl_window = NULL;

/** @brief Whether the platform should keep running; quit events clear it. */
static b8 sdl_running = TRUE;

/* Defined under Platform slots below; the unload hook calls it too. */
static void sdl_shutdown_platform(void);

/* ========================================================================== */
/* Memory                                                                     */
/*                                                                            */
/* One direction only: SDL's own allocations go UP into mg_alloc, tagged      */
/* PLATFORM, via SDL_SetMemoryFunctions, so SDL's internal usage shows in the */
/* tag ledger. Nothing routes back -- the engine's allocator is an app's to   */
/* install and defaults to libc, never to SDL -- so there is no cycle to      */
/* break. The byte moves below are a separate matter: those are slots this    */
/* gem fills because SDL's are vectorised.                                    */
/* ========================================================================== */

/** @brief Whether size survives the trip from u64 into size_t. */
static b8 sdl_mem_fits(const u64 size) {
    const size_t narrow = (size_t)size;
    return (u64)narrow == size;
}

/**
 * @brief SDL_malloc as the engine's malloc: routes into mg_alloc, so SDL's
 * internal allocations land on the tagged heap. SDL never passes a zero size.
 * size_t rather than len here and below: the signatures must match SDL's
 * SDL_malloc_func family exactly.
 * @param size How many bytes SDL asked for.
 * @return The allocation, or NULL on failure.
 */
static void* SDLCALL sdl_mg_malloc(size_t size) {
    return mg_alloc(1, size > 0 ? (len)size : 1, MG_MEMORY_TAG_PLATFORM);
}

/**
 * @brief SDL_calloc into mg_alloc, which already zeroes.
 * @param nmemb How many items SDL asked for.
 * @param size Bytes per item.
 * @return The zeroed allocation, or NULL on failure.
 */
static void* SDLCALL sdl_mg_calloc(size_t nmemb, size_t size) {
    return mg_alloc(nmemb > 0 ? (len)nmemb : 1, size > 0 ? (len)size : 1, MG_MEMORY_TAG_PLATFORM);
}

/**
 * @brief SDL_realloc into mg_resize, which keeps realloc semantics.
 * @param mem The block to resize; NULL allocates.
 * @param size The new byte count.
 * @return The possibly-moved block, or NULL on failure with mem untouched.
 */
static void* SDLCALL sdl_mg_realloc(void* mem, size_t size) {
    return mg_resize(mem, 1, size > 0 ? (len)size : 1, MG_MEMORY_TAG_PLATFORM);
}

/**
 * @brief SDL_free into mg_free. NULL is a no-op on both sides.
 * @param mem The block to release.
 */
static void SDLCALL sdl_mg_free(void* mem) {
    mg_free(mem, MG_MEMORY_TAG_PLATFORM);
}

/**
 * @brief Points SDL's allocator at the engine's. Only safe while SDL holds
 * no allocations, so a hot reload that left any live skips the redirect and
 * keeps SDL on whatever allocator those blocks came from.
 */
static void sdl_route_memory_to_magic(void) {
    if (SDL_GetNumAllocations() > 0) {
        MG_WARN("SDL already holds allocations; leaving its allocator as it is.");
        return;
    }
    SDL_SetMemoryFunctions(sdl_mg_malloc, sdl_mg_calloc, sdl_mg_realloc, sdl_mg_free);
}

/**
 * @brief gs_mem_zero via SDL_memset. A box with no data, no size, or a
 * size that does not fit a size_t comes back untouched.
 * @param b The box to zero.
 * @return b.
 */
static mg_box sdl_mem_zero(const mg_box b) {
    if (b.data != NULL && b.size > 0 && sdl_mem_fits(b.size)) {
        SDL_memset(b.data, 0, b.size);
    }
    return b;
}

/**
 * @brief gs_mem_fill via SDL_memset. The same guards as sdl_mem_zero.
 * @param b The box to fill.
 * @param value The byte to fill with.
 * @return b.
 */
static mg_box sdl_mem_fill(const mg_box b, const u8 value) {
    if (b.data != NULL && b.size > 0 && sdl_mem_fits(b.size)) {
        SDL_memset(b.data, value, b.size);
    }
    return b;
}

/**
 * @brief gs_mem_copy via SDL_memcpy.
 * Copies the smaller of the two sizes, so neither box is overrun.
 * @param dst The box copied into.
 * @param src The box copied from.
 * @return dst.
 */
static mg_box sdl_mem_copy(const mg_box dst, const mg_box src) {
    const u64 size = src.size < dst.size ? src.size : dst.size;
    if (dst.data != NULL && src.data != NULL && size > 0 && sdl_mem_fits(size)) {
        SDL_memcpy(dst.data, src.data, (size_t)size);
    }
    return dst;
}

/**
 * @brief gs_mem_move via SDL_memmove. SDL_memcpy's overlapping twin, and
 * the reason move is its own slot: filling copy with a vectorised memcpy must
 * not quietly take the overlapping case with it.
 * @param dst The box copied into.
 * @param src The box copied from.
 * @return dst.
 */
static mg_box sdl_mem_move(const mg_box dst, const mg_box src) {
    const u64 size = src.size < dst.size ? src.size : dst.size;
    if (dst.data != NULL && src.data != NULL && size > 0 && sdl_mem_fits(size)) {
        SDL_memmove(dst.data, src.data, (size_t)size);
    }
    return dst;
}

/* ========================================================================== */
/* Lifecycle                                                                  */
/* ========================================================================== */

/**
 * @brief Prepares the gem when the engine loads it: SDL's allocator first,
 * while SDL has yet to allocate, then SDL itself.
 * @param owner The app the gem was loaded into.
 */
static void sdl_on_load(mg_app* owner) {
    MG_UNUSED(owner);

    sdl_route_memory_to_magic();

    /* Wide open: which severities reach the console is the engine's call,
     * made at compile time in the MG_* log macros, not SDL's. */
    SDL_SetLogPriorities(SDL_LOG_PRIORITY_TRACE);

    SDL_Init(SDL_INIT_VIDEO);
}

/**
 * @brief Tears the gem down when the engine unloads it.
 * @param owner The app the gem was loaded into.
 */
static void sdl_on_unload(mg_app* owner) {
    MG_UNUSED(owner);
    /* Called directly, not through the slot: by unload time the loader may
     * have already put the slot back to its default. An unload without a
     * shutdown still releases SDL this way. */
    sdl_shutdown_platform();
}

/* ========================================================================== */
/* Platform slots                                                             */
/* ========================================================================== */

/**
 * @brief gs_platform_setup via SDL.
 * @param initial_state The state to set the platform up with.
 * @return True on success.
 */
static b8 sdl_setup_platform(const mg_platform_state* initial_state) {
    if (sdl_window != NULL) {
        MG_WARN("SDL platform set up twice; ignoring the second setup.");
        return TRUE;
    }

    if (!SDL_Init(SDL_INIT_VIDEO)) {
        MG_ERROR("SDL_Init failed: %s", SDL_GetError());
        return FALSE;
    }

    mg_str_view title = mg_view_cstr(SDL_GEM_DEFAULT_TITLE);
    i32 width = SDL_GEM_DEFAULT_WIDTH;
    i32 height = SDL_GEM_DEFAULT_HEIGHT;
    if (initial_state != NULL) {
        if (!mg_str_view_empty(initial_state->name)) {
            title = initial_state->name;
        }
        if (initial_state->window.window_size.width > 0
            && initial_state->window.window_size.height > 0) {
            width = (i32)initial_state->window.window_size.width;
            height = (i32)initial_state->window.window_size.height;
        }
    }

    sdl_window = SDL_CreateWindow(mg_str_view_to_cstr(title), width, height, SDL_WINDOW_RESIZABLE);
    if (sdl_window == NULL) {
        MG_ERROR("SDL_CreateWindow failed: %s", SDL_GetError());
        SDL_Quit();
        return FALSE;
    }

    sdl_running = TRUE;
    return TRUE;
}

/**
 * @brief gs_platform_update via SDL: applies engine-set state to the live
 * window, the pump's mirror.
 * @param state The state to apply; NULL applies nothing.
 * @return True when a window was up to take it.
 */
static b8 sdl_update_platform(const mg_platform_state* state) {
    if (sdl_window == NULL || state == NULL) {
        return FALSE;
    }

    if (!mg_str_view_empty(state->name)) {
        SDL_SetWindowTitle(sdl_window, mg_str_view_to_cstr(state->name));
    }
    if (state->window.window_size.width > 0 && state->window.window_size.height > 0) {
        SDL_SetWindowSize(
            sdl_window,
            (i32)state->window.window_size.width,
            (i32)state->window.window_size.height);
    }
    /* Mode 0 is windowed; anything else is fullscreen, until modes get names. */
    SDL_SetWindowFullscreen(sdl_window, state->window.window_mode != 0);
    return TRUE;
}

/** @brief gs_platform_cleanup via SDL. Safe to call when never set up. */
static void sdl_shutdown_platform(void) {
    if (sdl_window != NULL) {
        SDL_DestroyWindow(sdl_window);
        sdl_window = NULL;
    }
    SDL_Quit();
}

/**
 * @brief The magic event an SDL event maps to, or FALSE for one that maps to
 * nothing. Payloads stay empty boxes until the engine defines their shapes.
 * @param event The SDL event to translate.
 * @param out_event Receives the translation when there is one.
 * @return True when the event translated.
 */
static b8 sdl_translate_event(const SDL_Event* event, mg_event* out_event) {
    mg_event_code type = MG_EVENT_CODE_NONE;

    switch (event->type) {
        case SDL_EVENT_QUIT:
        case SDL_EVENT_WINDOW_CLOSE_REQUESTED:
            type = MG_EVENT_CODE_APP_QUITING;
            break;
        case SDL_EVENT_WINDOW_SHOWN:
            type = MG_EVENT_CODE_WINDOW_OPENED;
            break;
        case SDL_EVENT_WINDOW_DESTROYED:
            type = MG_EVENT_CODE_WINDOW_CLOSED;
            break;
        case SDL_EVENT_WINDOW_MINIMIZED:
            type = MG_EVENT_CODE_WINDOW_MINIMIZED;
            break;
        case SDL_EVENT_WINDOW_RESIZED:
            type = MG_EVENT_CODE_WINDOW_RESIZED;
            break;
        case SDL_EVENT_KEYBOARD_ADDED:
        case SDL_EVENT_MOUSE_ADDED:
            type = MG_EVENT_CODE_INPUT_DEVICE_CONNECTED;
            break;
        case SDL_EVENT_KEYBOARD_REMOVED:
        case SDL_EVENT_MOUSE_REMOVED:
            type = MG_EVENT_CODE_INPUT_DEVICE_DISCONNECTED;
            break;
        case SDL_EVENT_KEY_DOWN:
        case SDL_EVENT_MOUSE_BUTTON_DOWN:
            type = MG_EVENT_CODE_INPUT_PRESSED;
            break;
        case SDL_EVENT_KEY_UP:
        case SDL_EVENT_MOUSE_BUTTON_UP:
            type = MG_EVENT_CODE_INPUT_RELEASED;
            break;
        case SDL_EVENT_MOUSE_MOTION:
            type = MG_EVENT_CODE_INPUT_MOUSE_MOVED;
            break;
        default:
            return FALSE;
    }

    out_event->code = (u32)type;
    out_event->data = (mg_box) {.data = NULL, .size = 0};
    return TRUE;
}

/**
 * @brief gs_platform_poll_event via SDL: the next SDL event that maps to
 * an engine event, translated. SDL events that map to nothing are consumed
 * and skipped; a quit comes out like the rest and lowers the running flag,
 * so the pump reports it the same frame the engine hears the event. The
 * engine does the queueing -- this gem's own event registry is not the
 * engine's, so queueing here would fill a queue nobody drains.
 * @param out_event Receives the translated event when one came out.
 * @return TRUE when out_event holds an event; FALSE when none are pending.
 */
static b8 sdl_poll_event(mg_event* out_event) {
    SDL_Event event;
    mg_event translated;
    while (SDL_PollEvent(&event)) {
        if (!sdl_translate_event(&event, &translated)) {
            continue;
        }
        if (translated.code == MG_EVENT_CODE_APP_QUITING) {
            sdl_running = FALSE;
        }
        if (out_event != NULL) {
            *out_event = translated;
        }
        return TRUE;
    }
    return FALSE;
}

/**
 * @brief gs_platform_pump via SDL: reports the window and screen, and
 * whether the platform should keep running. The events themselves come out
 * through sdl_poll_event, which the engine drains first.
 * @param out_state Receives the platform state after the pump.
 * @return True while the platform should keep running.
 */
static b8 sdl_pump_platform(mg_platform_state* out_state) {
    if (out_state != NULL && sdl_window != NULL) {
        i32 width = 0;
        i32 height = 0;
        SDL_GetWindowSize(sdl_window, &width, &height);
        out_state->name = mg_view_cstr(SDL_GetPlatform());
        out_state->window.window_size.width = (u32)width;
        out_state->window.window_size.height = (u32)height;
    }

    return sdl_running;
}

/**
 * @brief gs_platform_write_to_console via SDL's log.
 * @param level The severity of the message.
 * @param file The file the message came from.
 * @param line The line the message came from.
 * @param message The message itself.
 */
static void sdl_write_to_console(
    const mg_log_level level,
    const char* file,
    const i32 line,
    const char* message) {
    static const SDL_LogPriority priorities[] = {
        SDL_LOG_PRIORITY_TRACE,
        SDL_LOG_PRIORITY_DEBUG,
        SDL_LOG_PRIORITY_INFO,
        SDL_LOG_PRIORITY_WARN,
        SDL_LOG_PRIORITY_ERROR,
        SDL_LOG_PRIORITY_CRITICAL,
    };

    const i32 index = (i32)level;
    const i32 count = (i32)(sizeof(priorities) / sizeof(priorities[0]));
    const SDL_LogPriority priority =
        index >= 0 && index < count ? priorities[index] : SDL_LOG_PRIORITY_INFO;
    SDL_LogMessage(SDL_LOG_CATEGORY_APPLICATION, priority, "%s (%s:%d)", message, file, line);
}

/**
 * @brief gs_platform_sleep via SDL.
 * @param ms How long to sleep, in milliseconds.
 */
static void sdl_sleep(const ts ms) {
    if (ms > 0) {
        SDL_DelayNS((u64)(ms * 1.0e6));
    }
}

/**
 * @brief gs_platform_get_timestamp via SDL.
 * @return The time the platform has been ticking, in milliseconds.
 */
static ts sdl_get_timestamp(void) {
    const u64 ns = SDL_GetTicksNS();
    return (ts)ns / 1.0e6;
}

/**
 * @brief gs_platform_get_datetime via SDL.
 * @return The current local date and time, zeroed if the clock is unreadable.
 */
static mg_datetime sdl_get_datetime(void) {
    mg_datetime now = {0, 0, 0, 0, 0};

    SDL_Time time = 0;
    SDL_DateTime date = {0};
    if (SDL_GetCurrentTime(&time) && SDL_TimeToDateTime(time, &date, TRUE)) {
        now.milliseconds = (ts)date.nanosecond / 1.0e6;
        now.seconds = (ts)date.second;
        now.day = (ts)date.day;
        now.month = (ts)date.month;
        now.year = (ts)date.year;
    }

    return now;
}

/* ========================================================================== */
/* Filesystem slots                                                           */
/* ========================================================================== */

/**
 * @brief gs_platform_copy_file via SDL.
 * @param from The file to copy.
 * @param to Where the copy goes, overwritten if it exists.
 * @return True on success.
 */
static b8 sdl_copy_file(const char* from, const char* to) {
    return SDL_CopyFile(from, to) ? TRUE : FALSE;
}

/**
 * @brief gs_platform_delete_file via SDL.
 * @param path The file to delete.
 * @return True on success.
 */
static b8 sdl_delete_file(const char* path) {
    return SDL_RemovePath(path) ? TRUE : FALSE;
}

/**
 * @brief gs_platform_move_file via SDL.
 * @param from The file to move.
 * @param to Where it goes, overwritten if it exists.
 * @return True on success.
 */
static b8 sdl_move_file(const char* from, const char* to) {
    return SDL_RenamePath(from, to) ? TRUE : FALSE;
}

/**
 * @brief gs_platform_file_exists via SDL.
 * @param path The path to check.
 * @return True when a file sits at path; a directory there is not one.
 */
static b8 sdl_file_exists(const char* path) {
    SDL_PathInfo info;
    return SDL_GetPathInfo(path, &info) && info.type == SDL_PATHTYPE_FILE ? TRUE : FALSE;
}

/**
 * @brief gs_platform_dir_exists via SDL.
 * @param path The path to check.
 * @return True when a directory sits at path.
 */
static b8 sdl_dir_exists(const char* path) {
    SDL_PathInfo info;
    return SDL_GetPathInfo(path, &info) && info.type == SDL_PATHTYPE_DIRECTORY ? TRUE : FALSE;
}

/**
 * @brief gs_platform_read_dir via SDL.
 * @param path The directory to read.
 * @return The names in it, each an owned string; empty on failure. The caller
 * frees each name and then the list.
 */
static mg_str_list sdl_read_dir(const char* path) {
    i32 count = 0;
    char** entries = SDL_GlobDirectory(path, NULL, 0, &count);

    mg_str_list names = mg_str_list_new(entries != NULL ? (len)count : 0);
    for (i32 i = 0; entries != NULL && i < count; i++) {
        mg_str name = mg_str_new(mg_view_cstr(entries[i]));
        if (!mg_str_list_push(&names, name)) {
            mg_str_free(&name);
        }
    }

    SDL_free((void*)entries);
    return names;
}

/**
 * @brief gs_platform_read_txt_file via SDL. Characters past an
 * embedded NUL do not survive the trip; a text file has none.
 * @param path The file to read.
 * @return The contents as an owned string, or the NULL-charactered
 * out-of-memory string on failure.
 */
static mg_str sdl_read_txt_file(const char* path) {
    /* size_t, not len: SDL_LoadFile writes through a size_t pointer. */
    size_t size = 0;
    char* data = SDL_LoadFile(path, &size);
    if (data == NULL) {
        const mg_str none = {.items = NULL, .count = 0};
        return none;
    }

    const mg_str contents = mg_str_new(mg_view_cstr(data));
    SDL_free(data);
    return contents;
}

/**
 * @brief gs_platform_read_binary_file via SDL.
 * @param path The file to read.
 * @return The contents as an owned byte stream; empty on failure.
 */
static mg_byte_list sdl_read_binary_file(const char* path) {
    size_t size = 0;
    void* data = SDL_LoadFile(path, &size);

    mg_byte_list bytes = mg_byte_list_new(data != NULL && size > 0 ? (len)size : 0);
    if (data != NULL && bytes.items != NULL) {
        mg_buffer_push_many(&bytes, data, (len)size);
    }

    SDL_free(data);
    return bytes;
}

/* ========================================================================== */
/* Registration                                                               */
/* ========================================================================== */

MG_PROVIDE_GEM(
    "SDL",
    "1.0",
    "SDL platform backend",
    "Jeremy Hummel",
    sdl_on_load,
    NULL,
    NULL,
    sdl_on_unload)

MG_GEM_FILL_SLOTS(
    MG_GEM_FILL_SLOT(gs_platform_setup, sdl_setup_platform),
    MG_GEM_FILL_SLOT(gs_platform_cleanup, sdl_shutdown_platform),
    MG_GEM_FILL_SLOT(gs_platform_poll_event, sdl_poll_event),
    MG_GEM_FILL_SLOT(gs_platform_pump, sdl_pump_platform),
    MG_GEM_FILL_SLOT(gs_platform_update, sdl_update_platform),
    MG_GEM_FILL_SLOT(gs_mem_zero, sdl_mem_zero),
    MG_GEM_FILL_SLOT(gs_mem_fill, sdl_mem_fill),
    MG_GEM_FILL_SLOT(gs_mem_copy, sdl_mem_copy),
    MG_GEM_FILL_SLOT(gs_mem_move, sdl_mem_move),
    MG_GEM_FILL_SLOT(gs_platform_write_to_console, sdl_write_to_console),
    MG_GEM_FILL_SLOT(gs_platform_sleep, sdl_sleep),
    MG_GEM_FILL_SLOT(gs_platform_get_timestamp, sdl_get_timestamp),
    MG_GEM_FILL_SLOT(gs_platform_get_datetime, sdl_get_datetime),
    MG_GEM_FILL_SLOT(gs_platform_copy_file, sdl_copy_file),
    MG_GEM_FILL_SLOT(gs_platform_delete_file, sdl_delete_file),
    MG_GEM_FILL_SLOT(gs_platform_move_file, sdl_move_file),
    MG_GEM_FILL_SLOT(gs_platform_file_exists, sdl_file_exists),
    MG_GEM_FILL_SLOT(gs_platform_dir_exists, sdl_dir_exists),
    MG_GEM_FILL_SLOT(gs_platform_read_dir, sdl_read_dir),
    MG_GEM_FILL_SLOT(gs_platform_read_txt_file, sdl_read_txt_file),
    MG_GEM_FILL_SLOT(gs_platform_read_binary_file, sdl_read_binary_file))
