// Follows the default output device clock for ~30 s and prints what the driver would use.
// Plays only silence (shared mode). Usage: ClockFollowerChecks.exe [endpoint id]
#include "asio_driver/DeviceClockFollower.h"
#include <cstdio>
#include <string>

int wmain(int argc, wchar_t** argv)
{
    elka::asio::DeviceClockFollower follower;
    follower.start(argc > 1 ? std::wstring(argv[1]) : std::wstring());
    const char* names[] = { "off", "measuring", "locked", "device not found", "device unavailable" };
    int finalState = 0;
    double finalRatio = 1.0;
    for (int second = 1; second <= 30; ++second)
    {
        Sleep(1000);
        finalState = static_cast<int>(follower.state());
        finalRatio = follower.ratio();
        std::printf("%2d s  %-18s  %+8.3f ppm\n", second, names[finalState], (finalRatio - 1.0) * 1e6);
    }
    follower.stop();
    const bool ok = finalState == 2 && finalRatio > 0.999 && finalRatio < 1.001;
    std::printf("%s\n", ok ? "PASS: locked to the device clock" : "FAIL: did not lock");
    return ok ? 0 : 1;
}
