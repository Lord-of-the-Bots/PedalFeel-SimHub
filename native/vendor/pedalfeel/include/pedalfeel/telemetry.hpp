#pragma once

#include "pedalfeel/types.hpp"

namespace pedalfeel {

class TelemetrySource {
  public:
    virtual ~TelemetrySource() = default;
    [[nodiscard]] virtual VehicleState sample(double dtSeconds) = 0;
    virtual void seek(double normalizedPosition) = 0;
};

} // namespace pedalfeel
