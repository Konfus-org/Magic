/**
 * @file buffer.c
 * @author Jeremy Hummel (konfusdev@gmail.com)
 * @brief
 * @version 1.0
 * @date 2026-09-08
 * @copyright MagicGameEngine is Copyright (c) Jeremy Hummel
 */

#include "magic/collections/buffer.h"
#include "magic/debug/logging.h"
#include "magic/memory/copy.h"
#include <string.h>

/* logging.h is here for MG_FATAL, which the assert macros expand to;
 * asserts.h cannot include it itself without closing an include loop.
 *
 * void* has no size, so [] does not compile on it: every function below walks
 * with byte* and the buffer's item_size as the stride, and always lands on the
 * address of an curr, never the curr itself. */

mg_buffer mg_buffer_new(
    const len item_size,
    const len capacity,
    const mg_destructor destructor,
    const enum mg_memory_tag tag) {
    mg_buffer buffer = {NULL, item_size, 0, 0, destructor};
    MG_ASSERT_RETURN_VAL_IF(item_size > 0, buffer, "a buffer needs an curr size");

    if (capacity > 0) {
        buffer.items = mg_alloc(capacity, item_size, tag);
        if (buffer.items != NULL) {
            buffer.capacity = capacity;
        }
    }
    return buffer;
}

void mg_buffer_free(mg_buffer* buffer, const enum mg_memory_tag tag) {
    MG_ASSERT_RETURN_IF(buffer != NULL, "buffer must not be NULL");

    /* Destroy the live items while their bytes are still readable; only then
     * may the storage go back. clear also zeroes the count. */
    mg_buffer_clear(buffer);
    mg_free(buffer->items, tag);
    buffer->items = NULL;
    buffer->capacity = 0;
}

mg_view mg_buffer_view(const mg_buffer* buffer) {
    const mg_view none = {NULL, 0, 0};
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, none, "buffer must not be NULL");

    const mg_view view = {buffer->items, buffer->item_size, buffer->count};
    return view;
}

b8 mg_buffer_empty(const mg_buffer* buffer) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, TRUE, "buffer must not be NULL");
    return buffer->count == 0;
}

b8 mg_buffer_full(const mg_buffer* buffer) {
    /* TRUE for a NULL buffer: the answer that stops an append going through a
     * null pointer. */
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, TRUE, "buffer must not be NULL");
    return buffer->count >= buffer->capacity;
}

b8 mg_buffer_contains(const mg_buffer* buffer, const void* item) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, FALSE, "buffer must not be NULL");

    const byte* base = buffer->items;

    /* Identity, not equality: whether curr points at one of our slots, not
     * whether an equal curr is stored in one. */
    for (len i = 0; i < buffer->count; i++)
        if (base + (i * buffer->item_size) == item)
            return TRUE;

    return FALSE;
}

ind mg_buffer_index_of(const mg_buffer* buffer, const void* item, const len item_size) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, MG_NOT_FOUND, "buffer must not be NULL");
    MG_ASSERT_RETURN_VAL_IF(item != NULL, MG_NOT_FOUND, "curr must not be NULL");
    MG_ASSERT_RETURN_VAL_IF(
        item_size == buffer->item_size,
        MG_NOT_FOUND,
        "searching for an curr whose size is not the buffer's curr size");

    const byte* base = buffer->items;
    for (len i = 0; i < buffer->count; i++) {
        if (memcmp(base + (i * buffer->item_size), item, buffer->item_size) == 0) {
            return i;
        }
    }
    return MG_NOT_FOUND;
}

/* Not a const mg_buffer*, unlike contains: the address handed back is
 * writable, so promising not to touch the buffer is a promise this cannot keep. */
void* mg_buffer_at(mg_buffer* buffer, ind index) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, NULL, "buffer must not be NULL");

    /* No index < 0 half: ind is unsigned, so it could never be true. */
    MG_ASSERT_RETURN_VAL_IF(index < buffer->count, NULL, "index out of range");

    byte* base = buffer->items;
    return base + (index * buffer->item_size);
}

void* mg_buffer_at_sized(mg_buffer* buffer, const ind index, const len item_size) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, NULL, "buffer must not be NULL");
    MG_ASSERT_RETURN_VAL_IF(
        item_size == buffer->item_size,
        NULL,
        "reading as a type whose size is not the buffer's curr size");
    return mg_buffer_at(buffer, index);
}

void mg_buffer_push(mg_buffer* buffer, const void* item, const len item_size) {
    MG_ASSERT_RETURN_IF(buffer != NULL, "buffer must not be NULL");
    MG_ASSERT_RETURN_IF(
        item_size == buffer->item_size,
        "appending an curr whose size is not the buffer's curr size");
    MG_ASSERT_RETURN_IF(buffer->count < buffer->capacity, "buffer is full");

    byte* base = buffer->items;

    /* mg_mem_copy is (destination, source, size), so it reads as an
     * assignment: destination = source. */
    mg_mem_copy(base + (buffer->count * buffer->item_size), item, buffer->item_size);
    buffer->count++;
}

void mg_buffer_push_many(mg_buffer* buffer, const void* items, const len count) {
    MG_ASSERT_RETURN_IF(buffer != NULL, "buffer must not be NULL");
    if (count == 0) {
        return; /* nothing to copy, and items may rightfully be NULL */
    }
    MG_ASSERT_RETURN_IF(items != NULL, "items must not be NULL");
    MG_ASSERT_RETURN_IF(
        count <= buffer->capacity - buffer->count,
        "buffer has no room for count more items");

    byte* base = buffer->items;
    mg_mem_copy(base + (buffer->count * buffer->item_size), items, count * buffer->item_size);
    buffer->count += count;
}

void* mg_buffer_peek(mg_buffer* buffer) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, NULL, "buffer must not be NULL");

    /* mg_last and not at(count - 1): on an empty buffer the subtraction
     * would wrap to a huge index, and the promise here is a quiet NULL. */
    return mg_last(buffer);
}

b8 mg_buffer_pop(mg_buffer* buffer, void* out) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, FALSE, "buffer must not be NULL");

    /* Popping nothing is a quiet FALSE, in the manner of free(NULL). */
    void* last = mg_last(buffer);
    if (last == NULL) {
        return FALSE;
    }

    /* Copied out whole, then the slot is scrubbed: the buffer keeps no stale
     * bytes for a later read to trip over. The curr is given over, not
     * destroyed -- ownership moves to the popper, so no destructor runs;
     * remove and clear are the operations that destroy. */
    if (out != NULL) {
        mg_mem_copy(out, last, buffer->item_size);
    }
    mg_mem_zero(last, buffer->item_size);
    buffer->count--;
    return TRUE;
}

b8 mg_buffer_pop_front(mg_buffer* buffer, void* out) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, FALSE, "buffer must not be NULL");

    byte* first = mg_first(buffer);
    if (first == NULL) {
        return FALSE;
    }

    if (out != NULL) {
        mg_mem_copy(out, first, buffer->item_size);
    }

    /* Shift everything after the front down one slot, one curr at a time:
     * adjacent slots never overlap, which one whole-region copy could not
     * promise. This is the O(n) that buys FIFO order; pop's stack end is the
     * O(1) that does not need it. */
    byte* base = buffer->items;
    for (len i = 1; i < buffer->count; i++) {
        byte* slot = base + (i * buffer->item_size);
        mg_mem_copy(slot - buffer->item_size, slot, buffer->item_size);
    }

    /* The vacated slot is the last one after the shift; scrubbed like pop's. */
    mg_mem_zero(mg_last(buffer), buffer->item_size);
    buffer->count--;
    return TRUE;
}

void mg_buffer_insert(mg_buffer* buffer, const ind index, const void* item, const len item_size) {
    MG_ASSERT_RETURN_IF(buffer != NULL, "buffer must not be NULL");
    MG_ASSERT_RETURN_IF(
        item_size == buffer->item_size,
        "inserting an curr whose size is not the buffer's curr size");
    MG_ASSERT_RETURN_IF(index < buffer->count, "index out of range");
    MG_ASSERT_RETURN_IF(buffer->count < buffer->capacity, "buffer is full");

    byte* base = buffer->items;
    byte* target = base + (index * buffer->item_size);
    byte* free_slot = base + (buffer->count * buffer->item_size);

    /* Move the occupant out to the free slot past the end rather than shifting
     * everything right by one; that is what makes insert unordered and O(1). */
    mg_mem_copy(free_slot, target, buffer->item_size);
    mg_mem_copy(target, item, buffer->item_size);
    buffer->count++;
}

void mg_buffer_remove(mg_buffer* buffer, ind index) {
    MG_ASSERT_RETURN_IF(buffer != NULL, "buffer must not be NULL");
    MG_ASSERT_RETURN_IF(index < buffer->count, "index out of range");

    byte* base = buffer->items;
    byte* target = base + (index * buffer->item_size);

    /* 1. Let the curr release whatever it owns, while it is still readable. */
    if (buffer->destructor != NULL) {
        buffer->destructor(target);
    }

    /* 2. Fill the gap with the last curr, unless the removed curr is the last:
     * copying a region onto itself is undefined. */
    byte* last = base + ((buffer->count - 1) * buffer->item_size);
    if (target != last) {
        mg_mem_copy(target, last, buffer->item_size);
    }

    /* 3. One shorter, or the curr just moved would still sit at the end too. */
    buffer->count--;
}

void mg_buffer_clear(mg_buffer* buffer) {
    MG_ASSERT_RETURN_IF(buffer != NULL, "buffer must not be NULL");

    /* Tested once, outside the walk: plain data has no destructor to run. */
    if (buffer->destructor != NULL) {
        void* item;
        mg_foreach(item, buffer) {
            buffer->destructor(item);
        }
    }

    /* After the walk: FOREACH re-reads count every iteration, so zeroing it in
     * the body would end the walk early. */
    buffer->count = 0;
}

b8 mg_buffer_reserve(mg_buffer* buffer, const len needed, const enum mg_memory_tag tag) {
    MG_ASSERT_RETURN_VAL_IF(buffer != NULL, FALSE, "buffer must not be NULL");
    MG_ASSERT_RETURN_VAL_IF(buffer->item_size > 0, FALSE, "buffer has no curr size");

    if (needed <= buffer->capacity) {
        return TRUE;
    }

    /* Doubling from whatever the buffer holds now, with 0 stepping to 1 so the
     * loop has something to double. No floor: the first allocation's size is
     * the caller's opinion, not this function's. */
    len next = buffer->capacity == 0 ? 1 : buffer->capacity;
    while (next < needed) {
        /* Doubling past U64_MAX / 2 would wrap and never end the loop, so ask
         * for the exact figure instead. */
        if (next > U64_MAX / 2) {
            next = needed;
            break;
        }
        next *= 2;
    }

    /* mg_resize takes NULL, where it is malloc, so a buffer that has never
     * allocated grows with no special case. On failure the old block is still
     * valid, leaving the buffer exactly as it was. */
    void* items = mg_resize_buffer(buffer->items, next, buffer->item_size, tag);
    if (items == NULL) {
        return FALSE;
    }

    buffer->items = items;
    buffer->capacity = next;
    return TRUE;
}

void mg_buffer_resize(mg_buffer* buffer, len new_capacity, const enum mg_memory_tag tag) {
    MG_ASSERT_RETURN_IF(buffer != NULL, "buffer must not be NULL");
    if (new_capacity == buffer->capacity)
        return;

    /* Destroy what no longer fits, while the items are still readable. Bounded
     * by count, not capacity: the slots between the two never held an curr. */
    if (new_capacity < buffer->count) {
        if (buffer->destructor != NULL) {
            byte* base = buffer->items;
            for (len i = new_capacity; i < buffer->count; i++) {
                buffer->destructor(base + (i * buffer->item_size));
            }
        }
        buffer->count = new_capacity;
    }

    /* A resize to nothing is a free, and has to be spelled as one:
     * realloc(ptr, 0) may return NULL having freed the block, which is
     * indistinguishable from the failure the check below reads it as. */
    if (new_capacity == 0) {
        mg_free(buffer->items, tag);
        buffer->items = NULL;
        buffer->capacity = 0;
        return;
    }

    void* items = mg_resize_buffer(buffer->items, new_capacity, buffer->item_size, tag);

    /* On failure the old block is still valid and still ours; assigning NULL
     * into items would leak it. */
    MG_ASSERT_RETURN_IF(items != NULL, "failed to resize buffer");

    buffer->items = items;
    buffer->capacity = new_capacity;
}
