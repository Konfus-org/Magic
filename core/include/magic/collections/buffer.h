/**
 * @file buffer.h
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief Generic byte storage, and the indexing every container built on it shares.
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#pragma once
#include "magic/collections/view.h"
#include "magic/export.h"
#include "magic/memory/alloc.h"
#include "magic/primitives.h"

/**
 * @brief The index no item has: what a search hands back when nothing
 * matched, and the value to compare against before using one.
 */
#define MG_NOT_FOUND ((ind) - 1)

/**
 * @brief The item at index as a type*, semi-safely: asserts that sizeof(type)
 * is the buffer's own item size, which catches reading as a wrong-sized type
 * but not as a wrong type of the same size.
 * @param buffer The buffer, or anything that IS one: an array or a list.
 * @param index Which item.
 * @param type The type to read the item as.
 * @return The item's address as a type*, or NULL if index is past the end.
 * @code
 * u32* score = mg_at(&scores, 0, u32);
 * @endcode
 */
#define mg_at(buffer, index, type) ((type*)mg_buffer_at_sized((buffer), (index), sizeof(type)))

/** @brief The address of the first live item, or NULL when the buffer is empty. */
#define mg_first(buffer) ((buffer)->count > 0 ? (buffer)->items : NULL)

/** @brief The address of the last live item, or NULL when the buffer is empty. */
#define mg_last(buffer)                                                                            \
    ((buffer)->count > 0                                                                           \
         ? (void*)((byte*)(buffer)->items + (((buffer)->count - 1) * (buffer)->item_size))         \
         : NULL)

/**
 * @brief The zeroed initializer in both languages these headers compile
 * as: C requires the 0 a brace-enclosed scalar allows and C++ refuses to
 * hand an enum member, so C++ value-initializes through empty braces.
 */
#ifdef __cplusplus
    #define MG_ZERO_INIT_                                                                          \
        {}
#else
    #define MG_ZERO_INIT_ {0}
#endif

/**
 * @brief Points item at address without naming item's type. In C a void*
 * assigns to any object pointer implicitly; the C++ of the unit tests forbids
 * that, so there the type is recovered from item with decltype.
 */
#ifdef __cplusplus
    #define MG_FOREACH_BIND_(curr, address) ((curr) = (decltype(curr))(void*)(address))
#else
    #define MG_FOREACH_BIND_(item, address) ((item) = (void*)(address))
#endif

/**
 * @brief Walks every live item, pointing item at each in turn. Declare item
 * before the loop as a pointer to the buffer's item type; the walk takes its
 * type from that declaration. A plain for loop, so break and continue work as
 * usual. Do not change the buffer's count inside the body.
 */
#define mg_foreach(item, buffer)                                                                   \
    for (MG_FOREACH_BIND_(item, (buffer)->items);                                                  \
         (byte*)(item) < (byte*)(buffer)->items + ((buffer)->count * (buffer)->item_size);         \
         MG_FOREACH_BIND_(item, (byte*)(item) + (buffer)->item_size))

/** @brief mg_foreach over only the items cond holds for. */
#define mg_where(item, buffer, cond) mg_foreach(item, buffer) if (cond)

/**
 * @brief Capacity slots of item_size bytes, the first count of them live.
 * The arrays and lists ARE this struct, so every function here works on one
 * of those directly.
 */
typedef struct mg_buffer {
    void* items; /**< The allocation being described. */
    len item_size; /**< Bytes per slot, and the stride between them. */
    len count; /**< How many slots at the front hold live items. */
    len capacity; /**< How many slots the allocation has room for. */
    mg_destructor destructor; /**< Run on each curr the buffer destroys; NULL for plain data. */
} mg_buffer;

/**
 * @brief A buffer over fresh zeroed storage created at the expansion site:
 * capacity slots of type, starting empty, allocating nothing. A lowercase
 * macro in the manner of foreach, not a function, because a C global must be
 * initialized by a constant expression, and no function call is one; the
 * storage is a compound literal, whose address is. At file scope the storage
 * lives for the whole program; inside a function it dies with the enclosing
 * block, so never return one made there. Never free the result either way:
 * the storage never came from mg_alloc. Initializes anything a
 * mg_buffer underlies; mg_array_construct is the array spelling. The
 * destructor starts NULL; for items that own something, assign the field
 * before the first remove or clear.
 * @param type Item type.
 * @param capacity How many slots; a constant, like any C array's length.
 * @code
 * static mg_buffer g_scratch = mg_buffer_construct(byte, 256);
 * @endcode
 */
#define mg_buffer_construct(type, capacity)                                                        \
    {((type[(capacity)]) {0}), sizeof(type), 0, (capacity), NULL}

/**
 * @brief A buffer over a fresh allocation of capacity slots, starting empty.
 * @param item_size Bytes per slot.
 * @param capacity How many slots; 0 allocates nothing.
 * @param destructor Run on each item the buffer destroys through remove,
 * clear, resize or free; NULL for plain data. Stored once here, never passed
 * again.
 * @param tag The tag the storage is accounted under; free with the same one.
 * @return The buffer. Out of memory comes back with a NULL items and a
 * capacity of 0: a buffer that is already full.
 */
MG_EXPORT mg_buffer
    mg_buffer_new(len item_size, len capacity, mg_destructor destructor, enum mg_memory_tag tag);

/**
 * @brief Destroys the live items with the buffer's destructor, then releases
 * an allocation that came from mg_buffer_new or grew through reserve or
 * resize. Leaves the zero-capacity buffer, so freeing twice is a no-op. Never
 * for a constructed buffer, whose storage was never allocated.
 * @param tag The tag the storage was allocated under.
 */
MG_EXPORT void mg_buffer_free(mg_buffer* buffer, enum mg_memory_tag tag);

/**
 * @brief A view of a buffer's live items. Borrows from the buffer, so
 * growing, resizing or freeing it leaves the view dangling.
 */
MG_EXPORT mg_view mg_buffer_view(const mg_buffer* buffer);

/** @brief Whether the buffer holds no items. */
MG_EXPORT b8 mg_buffer_empty(const mg_buffer* buffer);

/** @brief Whether the buffer has no room left. Pushing onto a full buffer asserts. */
MG_EXPORT b8 mg_buffer_full(const mg_buffer* buffer);

/** @brief Whether item is the address of one of the buffer's live slots. */
MG_EXPORT b8 mg_buffer_contains(const mg_buffer* buffer, const void* item);

/**
 * @brief The index of the first live item that equals item, byte for byte.
 * Byte equality is exact for primitives and pointers; a struct with padding
 * may compare unequal to an equal-fielded twin, since padding is nobody's.
 * @param item The value searched for, by address; not required to be one of
 * the buffer's own slots.
 * @param item_size sizeof what item points at; asserted against the buffer's
 * own item_size.
 * @return The first matching index, or MG_NOT_FOUND.
 */
MG_EXPORT ind mg_buffer_index_of(const mg_buffer* buffer, const void* item, len item_size);

/**
 * @brief The address of the item at index.
 * @return NULL if index is past the end.
 */
MG_EXPORT void* mg_buffer_at(mg_buffer* buffer, ind index);

/**
 * @brief mg_buffer_at, after asserting item_size is the buffer's own: the
 * check behind mg_at, which is the usual way to call it.
 * @return NULL if index is past the end or item_size is wrong.
 */
MG_EXPORT void* mg_buffer_at_sized(mg_buffer* buffer, ind index, len item_size);

/**
 * @brief Copies item into the first free slot, and grows the count by one.
 * @param item_size sizeof what item points at; asserted against the buffer's
 * own item_size, so an item of the wrong size is caught, not copied.
 */
MG_EXPORT void mg_buffer_push(mg_buffer* buffer, const void* item, len item_size);

/**
 * @brief Copies count items onto the end in one motion, growing the count by
 * count. Room for all of them is asserted, like push's one slot is.
 * @param items The first of count items of the buffer's own item size.
 */
MG_EXPORT void mg_buffer_push_many(mg_buffer* buffer, const void* items, len count);

/**
 * @brief The last live item, the one pop takes next: the top of the stack
 * the push end forms. Nothing is removed.
 * @return The item's address, or NULL when the buffer is empty.
 */
MG_EXPORT void* mg_buffer_peek(mg_buffer* buffer);

/**
 * @brief Removes the last item by copying it whole into out, then scrubs
 * the given-up slot to zeroes so no stale bytes linger. Ownership moves to
 * the popper, whatever the item owns included, so the buffer's destructor
 * does not run: remove and clear are the operations that destroy.
 * @param out Where the item lands, item_size bytes of room; NULL discards
 * the item unread.
 * @return TRUE when an item came out; FALSE when the buffer was empty.
 */
MG_EXPORT b8 mg_buffer_pop(mg_buffer* buffer, void* out);

/**
 * @brief mg_buffer_pop at the other end: removes the FIRST item, the
 * oldest pushed, so pop_front against push is a FIFO queue. What remains
 * keeps its order, every item shifting down one slot -- the O(n) that buys
 * order, where pop's stack end is O(1).
 * @param out Where the item lands, item_size bytes of room; NULL discards
 * the item unread.
 * @return TRUE when an item came out; FALSE when the buffer was empty.
 */
MG_EXPORT b8 mg_buffer_pop_front(mg_buffer* buffer, void* out);

/**
 * @brief Copies item over index, displacing what was there to the free slot.
 * @param item_size sizeof what item points at; asserted against the buffer's
 * own item_size, so an item of the wrong size is caught, not copied.
 */
MG_EXPORT void mg_buffer_insert(mg_buffer* buffer, ind index, const void* item, len item_size);

/**
 * @brief Destroys the item at index -- the buffer's own destructor runs on
 * it -- fills the gap with the last, shrinks the count.
 */
MG_EXPORT void mg_buffer_remove(mg_buffer* buffer, ind index);

/**
 * @brief Destroys all items with the buffer's own destructor, sets the count
 * to zero. The allocation stays; only free gives it back.
 */
MG_EXPORT void mg_buffer_clear(mg_buffer* buffer);

/**
 * @brief Grows the capacity by doubling until needed items fit.
 * Only for storage that came from mg_alloc, and the list's operation in
 * practice: growing an array breaks its fixed-capacity promise.
 * @param tag The tag the storage is accounted under; pass the one the owner
 * allocates and frees the same storage with, or the books will not balance.
 * @return False on out of memory, with the buffer left untouched.
 */
MG_EXPORT b8 mg_buffer_reserve(mg_buffer* buffer, len needed, enum mg_memory_tag tag);

/**
 * @brief Resizes the buffer to new_capacity. Any items outside the new
 * range are destroyed with the buffer's own destructor first.
 * @param tag The tag the storage is accounted under; pass the one the owner
 * allocates and frees the same storage with, or the books will not balance.
 */
MG_EXPORT void mg_buffer_resize(mg_buffer* buffer, len new_capacity, enum mg_memory_tag tag);
