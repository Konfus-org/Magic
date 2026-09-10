/**
 * @file array.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief A fixed-capacity array: a mg_buffer that allocates once and never
 * grows, minted per item type so the type is in the name and the signatures.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/buffer.h"
#include "magic/collections/iter.h"

/**
 * @brief Mints the typed spellings of an array of type, under name
 * exactly: the typedef, its iterator's, and the operations whose
 * signatures the item type improves. Everything minted is static inline, so
 * a mint lives in a header or a .c equally well; mint each name exactly once
 * per translation unit, or the definitions collide.
 * @param type Item type; sets the item size every operation checks against.
 * @param name The minted type name, used EXACTLY as given: nothing is pasted
 * onto it but the operation suffixes. This form is for a custom name that
 * says what the collection is for -- mg_event_code_table over _array. For the
 * conventional spelling, MG_DEFINE_ARRAY appends _array to the type's own
 * name instead.
 * @param destructor Run on each item the array destroys through remove,
 * clear, resize or free; NULL for plain data. Baked into _construct and _new,
 * so no operation takes one.
 * @code
 * MG_DEFINE_ARRAY(mg_logger, NULL); // -> mg_logger_array
 * MG_DEFINE_NAMED_ARRAY(mg_event_registration_list, mg_event_code_table, NULL); // -> mg_event_code_table
 *
 * mg_logger_array loggers = mg_logger_array_new(4);
 * mg_logger_array_push(&loggers, my_logger);
 * mg_logger_array_iter it = mg_logger_array_it(&loggers);
 * const mg_logger* logger;
 * while ((logger = mg_logger_array_iter_next(&it)) != NULL) { ... }
 * mg_logger_array_free(&loggers);
 * @endcode
 */
#define MG_DEFINE_NAMED_ARRAY(type, name, destructor)                                                \
    typedef mg_array name;                                                                           \
    typedef mg_iter name##_iter;                                                                     \
                                                                                                     \
    MG_MAYBE_UNUSED static inline name name##_construct(type* storage, const len capacity) {         \
        const name array =                                                                           \
            {(void*)storage, sizeof(type), 0, storage != NULL ? capacity : 0, (destructor)};         \
        return array;                                                                                \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline name name##_new(const len capacity) {                              \
        return mg_buffer_new(sizeof(type), capacity, (destructor), MG_MEMORY_TAG_ARRAY);             \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline void name##_free(name* array) {                                    \
        mg_buffer_free(array, MG_MEMORY_TAG_ARRAY);                                                  \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline void name##_push(name* array, type curr) {                         \
        mg_buffer_push(array, (const void*)&curr, sizeof(type));                                     \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline void name##_push_many(                                             \
        name* array,                                                                                 \
        const type* items,                                                                           \
        const len count) {                                                                           \
        mg_buffer_push_many(array, (const void*)items, count);                                       \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline type name##_pop(name* array) {                                     \
        type popped = MG_ZERO_INIT_;                                                                 \
        /* Cast spelled out: when type is itself a pointer, &popped erases two                     \
         * levels at once, which is only OK because the buffer copies bytes. */ \
        mg_buffer_pop(array, (void*)&popped);                                                        \
        return popped;                                                                               \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline type* name##_peek(name* array) {                                   \
        return (type*)mg_buffer_peek(array);                                                         \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline ind name##_index_of(const name* array, type curr) {                \
        return mg_buffer_index_of(array, (const void*)&curr, sizeof(type));                          \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline void name##_remove(name* array, const ind index) {                 \
        mg_buffer_remove(array, index);                                                              \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline type* name##_at(name* array, const ind index) {                    \
        return (type*)mg_buffer_at_sized(array, index, sizeof(type));                                \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline mg_view name##_view(const name* array) {                           \
        return mg_buffer_view(array);                                                                \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline name##_iter name##_it(const name* array) {                         \
        return mg_iter_construct(mg_buffer_view(array));                                             \
    }                                                                                                \
                                                                                                     \
    MG_MAYBE_UNUSED static inline name##_iter name##_rit(const name* array) {                        \
        return mg_iter_construct_reverse(mg_buffer_view(array));                                     \
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
 * @brief MG_DEFINE_NAMED_ARRAY under the conventional name: the type's own
 * spelling with _array appended, so MG_DEFINE_ARRAY(mg_logger, NULL) mints
 * mg_logger_array -- the type's own mg_ carries through, no prefix doubled.
 * For a type without the prefix, or a name of your own, use the NAMED form:
 * MG_DEFINE_NAMED_ARRAY(u32, mg_u32_array, NULL).
 */
#define MG_DEFINE_ARRAY(type, destructor) MG_DEFINE_NAMED_ARRAY(type, type##_array, destructor)

/**
 * @brief A fixed-capacity array: allocates once, and an item's address is
 * stable for the array's whole life. An array IS a mg_buffer, so everything
 * a mint does not spell -- mg_at, full, empty, clear, foreach -- is the
 * buffer's own, used directly.
 */
typedef mg_buffer mg_array;

/**
 * @brief An array over fresh zeroed storage created at the expansion site:
 * the buffer's constructor, spelled for arrays, whose fixed capacity the
 * storage's fixed length matches exactly. All its rules apply, above all
 * never freeing the result. The one form that initializes a file-scope
 * array: it is a constant expression, which C requires there and which the
 * minted construct, a function, can never be. @see mg_buffer_construct
 * @param type Item type; sets the item size every operation checks against.
 * @param capacity How many slots; a constant, like any C array's length.
 * @code
 * static mg_cmd_array g_cmds = mg_array_construct(mg_cmd, MG_MAX_CMDS);
 * @endcode
 */
#define mg_array_construct(type, capacity) mg_buffer_construct(type, capacity)

/* One array per primitive, the same set list.h mints; plain data, so no
 * destructor. Anything else mints its own where its curr type lives. */
MG_DEFINE_NAMED_ARRAY(u8, mg_u8_array, NULL);
MG_DEFINE_NAMED_ARRAY(u16, mg_u16_array, NULL);
MG_DEFINE_NAMED_ARRAY(u32, mg_u32_array, NULL);
MG_DEFINE_NAMED_ARRAY(u64, mg_u64_array, NULL);
MG_DEFINE_NAMED_ARRAY(i8, mg_i8_array, NULL);
MG_DEFINE_NAMED_ARRAY(i16, mg_i16_array, NULL);
MG_DEFINE_NAMED_ARRAY(i32, mg_i32_array, NULL);
MG_DEFINE_NAMED_ARRAY(i64, mg_i64_array, NULL);
MG_DEFINE_NAMED_ARRAY(f32, mg_f32_array, NULL);
MG_DEFINE_NAMED_ARRAY(f64, mg_f64_array, NULL);
MG_DEFINE_NAMED_ARRAY(b8, mg_b8_array, NULL);
MG_DEFINE_NAMED_ARRAY(b32, mg_b32_array, NULL);
MG_DEFINE_NAMED_ARRAY(ind, mg_ind_array, NULL);
MG_DEFINE_NAMED_ARRAY(addr, mg_addr_array, NULL);
MG_DEFINE_NAMED_ARRAY(len, mg_len_array, NULL);
MG_DEFINE_NAMED_ARRAY(byte, mg_byte_array, NULL);
MG_DEFINE_NAMED_ARRAY(err_code, mg_err_code_array, NULL);
