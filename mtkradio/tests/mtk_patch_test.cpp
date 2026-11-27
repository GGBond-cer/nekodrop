// SPDX-License-Identifier: GPL-2.0-only
#include "mtk_patch.h"
#include <array>
#include <cassert>
#include <fstream>
#include <iterator>
#include <vector>

int main(int argc, char **argv) {
  // Two type-2 sections. The second has one byte of alignment padding.
  constexpr size_t payload = MTK_PATCH_HEADER_SIZE + 2 * MTK_PATCH_DESCRIPTOR_SIZE;
  std::array<uint8_t, payload + 4> bytes{};
  bytes[47] = 2;
  for (unsigned i = 0; i < 2; ++i) {
    auto *d = bytes.data() + MTK_PATCH_HEADER_SIZE + i * MTK_PATCH_DESCRIPTOR_SIZE;
    d[3] = 2;
    d[7] = static_cast<uint8_t>(payload + 2 * i);
    d[11] = 2;
    d[15] = static_cast<uint8_t>(16 + i);
    d[19] = static_cast<uint8_t>(2 - i);
    d[20] = 1;
  }
  mtk_patch_image image{};
  assert(mtk_patch_open(bytes.data(), bytes.size(), &image) && image.count == 2);
  auto second = mtk_patch_region_at(&image, 1);
  assert(second.offset == payload + 2 && second.size == 2 && second.length == 1);
  assert(second.address == 17 && second.key == 0x01000000);
  assert(!mtk_patch_open(bytes.data(), bytes.size() - 1, &image));
  assert(!image.count && !image.descriptors);
  assert(!mtk_patch_open(bytes.data(), payload - 1, &image));
  assert(!mtk_patch_open(bytes.data(), MTK_PATCH_HEADER_SIZE - 1, &image));

  // A descriptor that reaches back into the section table must be rejected.
  bytes[MTK_PATCH_HEADER_SIZE + MTK_PATCH_DESCRIPTOR_SIZE + 7] = 1;
  assert(!mtk_patch_open(bytes.data(), bytes.size(), &image));
  for (int arg = 1; arg < argc; ++arg) {
    std::ifstream stream(argv[arg], std::ios::binary);
    assert(stream);
    std::vector<uint8_t> file{std::istreambuf_iterator<char>(stream), {}};
    assert(mtk_patch_open(file.data(), file.size(), &image));
  }
}
