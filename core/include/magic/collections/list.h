/**
 * @file list.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief A list: a mg_buffer that grows at runtime, minted per item type so
 * the type is in the name and the signatures.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/buffer.h"
#include "magic/collections/iter.h"

/**
 * @brief The capacity a list's first allocation reserves, so early appends
 * are one realloc and not a cascade of them. A list-level fact:
 * mg_buffer_reserve itself doubles from whatever the buffer holds.
 */
#define MG_LIST_MIN_CAPACITY 8

/**
 * @brief Mints the typed spellings of a list of type, under name
 * exactly: the typedef, its iterator's, and the operations whose
 * signatures the item type improves. Everything minted is static inline, so
 * a mint lives in a header or a .c equally well; mint each name exactly once
 * per translation unit, or the definitions collide.
 * @param type Item type; sets the item size every operation checks against.
 * @param name The minted type name, used EXACTLY as given: nothing is pasted
 * onto it but the operation suffixes. This form is for a custom name that
 * says what the collection is for. For the conventional spelling,
 * MG_DEFINE_LIST appends _list to the type's own name instead.
 * @param destructor Run on each item the list destroys through remove,
 * clear, resize or free; NULL for plain data. Baked into _construct and _new,
 * so no operation takes one.
 * @code
 * MG_DEFINE_LIST(mg_str, NULL); // -> mg_str_list
 *
 * mg_u32_list xs = mg_u32_list_new(0);
 * if (!mg_u32_list_push(&xs, 42)) { ... }
 * mg_u32_list_iter it = mg_u32_list_it(&xs);
 * const u32* x;
 * while ((x = mg_u32_list_iter_next(&it)) != NULL) { ... }
 * mg_u32_list_free(&xs);
 * @endcode
 */
#define MG_DEFINE_NAMED_LIST(type, name, destructor)                                                 \
    typedef mg_list name;                                                                            \
    typedef mg_iter name##_iter;                                                                     \
                                                                                                     \
    MG_MAYBE_UNUSED static inline name name##_construct(void) {                                      \
        const name list = {NULL, sizeof(type), 0, 0, (destructor)};                                  \
        return list;                                                                                 \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline name name##_new(const len capacity) {                              \
        return mg_buffer_new(sizeof(type), capacity, (destructor), MG_MEMORY_TAG_LIST);              \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline void name##_free(name* list) {                                     \
        mg_buffer_free(list, MG_MEMORY_TAG_LIST);                                                    \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline b8 name##_push(name* list, type curr) {                            \
        if (list == NULL) {                                                                          \
            return FALSE;                                                                            \
        }                                                                                            \
        const len needed = list->count + 1;                                                          \
        if (!mg_buffer_reserve(                                                                      \
                list,                                                                                \
                needed < MG_LIST_MIN_CAPACITY ? MG_LIST_MIN_CAPACITY : needed,                       \
                MG_MEMORY_TAG_LIST)) {                                                               \
            return FALSE;                                                                            \
        }                                                                                            \
        mg_buffer_push(list, (const void*)&curr, sizeof(type));                                      \
        return TRUE;                                                                                 \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline b8 name##_push_many(                                               \
        name* list,                                                                                  \
        const type* items,                                                                           \
        const len count) {                                                                           \
        if (list == NULL) {                                                                          \
            return FALSE;                                                                            \
        }                                                                                            \
        if (count == 0) {                                                                            \
            return TRUE;                                                                             \
        }                                                                                            \
        const len needed = list->count + count;                                                      \
        if (!mg_buffer_reserve(                                                                      \
                list,                                                                                \
                needed < MG_LIST_MIN_CAPACITY ? MG_LIST_MIN_CAPACITY : needed,                       \
                MG_MEMORY_TAG_LIST)) {                                                               \
            return FALSE;                                                                            \
        }                                                                                            \
        mg_buffer_push_many(list, (const void*)items, count);                                        \
        return TRUE;                                                                                 \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline type name##_pop(name* list) {                                      \
        type popped = MG_ZERO_INIT_;                                                                 \
        /* Cast spelled out: when type is itself a pointer, &popped erases two                     \
         * levels at once, which is only OK because the buffer copies bytes. */ \
        mg_buffer_pop(list, (void*)&popped);                                                         \
        return popped;                                                                               \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline type name##_pop_front(name* list) {                                \
        type popped = MG_ZERO_INIT_;                                                                 \
        mg_buffer_pop_front(list, (void*)&popped);                                                   \
        return popped;                                                                               \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline type* name##_peek(name* list) {                                    \
        return (type*)mg_buffer_peek(list);                                                          \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline ind name##_index_of(const name* list, type curr) {                 \
        return mg_buffer_index_of(list, (const void*)&curr, sizeof(type));                           \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline void name##_remove(name* list, const ind index) {                  \
        mg_buffer_remove(list, index);                                                               \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline type* name##_at(name* list, const ind index) {                     \
        return (type*)mg_buffer_at_sized(list, index, sizeof(type));                                 \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline mg_view name##_view(const name* list) {                            \
        return mg_buffer_view(list);                                                                 \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline name##_iter name##_it(const name* list) {                          \
        return mg_iter_construct(mg_buffer_view(list));                                              \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline name##_iter name##_rit(const name* list) {                         \
        return mg_iter_construct_reverse(mg_buffer_view(list));                                      \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline const type* name##_iter_next(name##_iter* it) {                    \
        return (const type*)mg_iter_next(it);                                                        \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline const type* name##_iter_previous(name##_iter* it) {                \
        return (const type*)mg_iter_previous(it);                                                    \
    }                                                                                                \
                                                                                                     \
    /* Eats the call site's semicolon; retyping a name to the same type is legal C11. */             \
    typedef name name

/**
 * @brief MG_DEFINE_NAMED_LIST under the conventional name: the type's own
 * spelling with _list appended, so MG_DEFINE_LIST(mg_str, NULL) mints
 * mg_str_list -- the type's own mg_ carries through, no prefix doubled. For a
 * type without the prefix, or a name of your own, use the NAMED form:
 * MG_DEFINE_NAMED_LIST(u32, mg_u32_list, NULL).
 */
#define MG_DEFINE_LIST(type, destructor) MG_DEFINE_NAMED_LIST(type, type##_list, destructor)

/**
 * @brief A list that grows as it is pushed onto; growing may move every
 * item, so no address in one is stable. A list IS a mg_buffer, so everything
 * a mint does not spell -- mg_at, empty, clear, foreach -- is the buffer's
 * own, used directly. No full to ask: a list grows instead, and push reports
 * out of memory.
 */
typedef mg_buffer mg_list;

/**
 * @brief An empty list of type as a constant expression, so it initializes a
 * file-scope list, which no function can: a C global's initializer may not
 * call one. No storage rides along, unlike the buffer and array forms: a
 * list's storage must come from mg_alloc so growing can realloc it, so the
 * first append allocates, and the minted free works as usual.
 * @param type Item type; sets the item size every operation checks against.
 * @code
 * static mg_str_list g_paths = mg_list_construct(mg_str);
 * @endcode
 */
#define mg_list_construct(type) {NULL, sizeof(type), 0, 0, NULL}

/* One list per primitive, the same set array.h mints; plain data, so no
 * destructor. mg_byte_list is the blob: binary file contents, gem reload
 * state, random bytes. Anything else mints its own where its curr type lives. */
MG_DEFINE_NAMED_LIST(u8, mg_u8_list, NULL);
MG_DEFINE_NAMED_LIST(u16, mg_u16_list, NULL);
MG_DEFINE_NAMED_LIST(u32, mg_u32_list, NULL);
MG_DEFINE_NAMED_LIST(u64, mg_u64_list, NULL);
MG_DEFINE_NAMED_LIST(i8, mg_i8_list, NULL);
MG_DEFINE_NAMED_LIST(i16, mg_i16_list, NULL);
MG_DEFINE_NAMED_LIST(i32, mg_i32_list, NULL);
MG_DEFINE_NAMED_LIST(i64, mg_i64_list, NULL);
MG_DEFINE_NAMED_LIST(f32, mg_f32_list, NULL);
MG_DEFINE_NAMED_LIST(f64, mg_f64_list, NULL);
MG_DEFINE_NAMED_LIST(b8, mg_b8_list, NULL);
MG_DEFINE_NAMED_LIST(b32, mg_b32_list, NULL);
MG_DEFINE_NAMED_LIST(ind, mg_ind_list, NULL);
MG_DEFINE_NAMED_LIST(addr, mg_addr_list, NULL);
MG_DEFINE_NAMED_LIST(len, mg_len_list, NULL);
MG_DEFINE_NAMED_LIST(byte, mg_byte_list, NULL);
MG_DEFINE_NAMED_LIST(err_code, mg_err_code_list, NULL);
