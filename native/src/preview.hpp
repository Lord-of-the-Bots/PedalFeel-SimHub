#pragma once
#include "engine.hpp"

namespace pedalfeel_plugin {
bool validPreviewEffect(int32_t effect) noexcept;
// Preconditions are validated by the public C entry point.
PfOutput preview(const PfConfig& config, int32_t effect, double elapsedSeconds) noexcept;
}
