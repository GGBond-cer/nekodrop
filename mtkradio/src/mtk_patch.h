/* SPDX-License-Identifier: BSD-3-Clause-Clear
 * Shared ROM patch descriptor format used by MediaTek Connac2 and Connac3.
 * MT7925 preflight checks adapted from mt76-based LuoboDrop firmware.c.
 * Copyright (C) 2023 MediaTek Inc.
 */
#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

enum { MTK_PATCH_HEADER_SIZE = 96, MTK_PATCH_DESCRIPTOR_SIZE = 64 };

typedef struct mtk_patch_image {
    const uint8_t *descriptors;
    uint32_t count;
} mtk_patch_image;

typedef struct mtk_patch_region {
    uint32_t offset, size, address, length, key;
} mtk_patch_region;

static inline uint32_t mtk_patch_be32(const uint8_t *p)
{
    return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3];
}

/* Only index an image validated by mtk_patch_open, with index < image->count. */
static inline mtk_patch_region mtk_patch_region_at(const mtk_patch_image *image, uint32_t index)
{
    const uint8_t *d = image->descriptors + (size_t)index * MTK_PATCH_DESCRIPTOR_SIZE;
    mtk_patch_region region = {mtk_patch_be32(d + 4), mtk_patch_be32(d + 8),
        mtk_patch_be32(d + 12), mtk_patch_be32(d + 16), mtk_patch_be32(d + 20)};
    return region;
}

/* Validate every section before the caller hands the first one to the MCU. */
static inline bool mtk_patch_open(const uint8_t *data, size_t bytes, mtk_patch_image *out)
{
    out->descriptors = NULL;
    out->count = 0;
    if (!data || bytes < MTK_PATCH_HEADER_SIZE) return false;

    const uint32_t count = mtk_patch_be32(data + 44);
    if (!count || count > (bytes - MTK_PATCH_HEADER_SIZE) / MTK_PATCH_DESCRIPTOR_SIZE)
        return false;

    const mtk_patch_image image = {data + MTK_PATCH_HEADER_SIZE, count};
    const size_t payload_start = MTK_PATCH_HEADER_SIZE + (size_t)count * MTK_PATCH_DESCRIPTOR_SIZE;
    for (uint32_t i = 0; i < count; ++i) {
        const uint8_t *d = image.descriptors + (size_t)i * MTK_PATCH_DESCRIPTOR_SIZE;
        if ((mtk_patch_be32(d) & 0xffffu) != 2u) return false;
        const mtk_patch_region region = mtk_patch_region_at(&image, i);
        if (region.offset < payload_start || region.offset > bytes ||
            region.size > bytes - region.offset || !region.length || region.length > region.size)
            return false;
    }
    *out = image;
    return true;
}
