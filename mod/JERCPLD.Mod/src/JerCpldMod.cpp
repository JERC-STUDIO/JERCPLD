#define LL_MEMORY_OPERATORS
#include "ll/api/memory/MemoryOperators.h"

#include "ll/api/mod/NativeMod.h"
#include "ll/api/mod/RegisterHelper.h"

#include <filesystem>
#include <fstream>
#include <string>

#include <nlohmann/json.hpp>

namespace jercpld {

constexpr char const* kModName  = "JERCPLD";
constexpr char const* kVersion  = "0.1.0";

struct Config {
    bool        translationEnabled = false;
    std::string serverVersion      = "1.26.51";
    int         serverProtocol     = 2193;
    int         acceptMinProtocol  = 1001; // 1.26.30 (社区数据)
    int         acceptMaxProtocol  = 2225; // 1.26.60
};

class JerCpldMod {
public:
    static JerCpldMod& getInstance() {
        static JerCpldMod instance;
        return instance;
    }

    JerCpldMod() : mSelf(*ll::mod::NativeMod::current()) {}

    [[nodiscard]] ll::mod::NativeMod& getSelf() const { return mSelf; }

    bool load() {
        auto& logger = getSelf().getLogger();
        loadConfig(logger);
        printBanner(logger);
        logger.info(
            "[{}] loaded v{} | server {}/{} | clients protocol {}-{} | translation={}",
            kModName,
            kVersion,
            mConfig.serverVersion,
            mConfig.serverProtocol,
            mConfig.acceptMinProtocol,
            mConfig.acceptMaxProtocol,
            mConfig.translationEnabled ? "ON" : "OFF"
        );
        return true;
    }

    bool enable() {
        getSelf().getLogger().info("[{}] enabled", kModName);
        return true;
    }

    bool disable() {
        getSelf().getLogger().info("[{}] disabled", kModName);
        return true;
    }

private:
    void loadConfig(ll::io::Logger& logger) {
        namespace fs = std::filesystem;
        try {
            auto configDir = getSelf().getConfigDir();
            if (configDir.empty()) {
                configDir = getSelf().getModDir() / "config";
            }
            fs::create_directories(configDir);
            auto configPath = configDir / "config.json";

            if (!fs::exists(configPath)) {
                std::ofstream out(configPath);
                out << "{\n"
                    << "  \"translationEnabled\": false,\n"
                    << "  \"serverVersion\": \"1.26.51\",\n"
                    << "  \"serverProtocol\": 2193,\n"
                    << "  \"acceptMinProtocol\": 1001,\n"
                    << "  \"acceptMaxProtocol\": 2225\n"
                    << "}\n";
            }

            std::ifstream in(configPath);
            nlohmann::json json;
            in >> json;
            mConfig.translationEnabled = json.value("translationEnabled", mConfig.translationEnabled);
            mConfig.serverVersion      = json.value("serverVersion", mConfig.serverVersion);
            mConfig.serverProtocol     = json.value("serverProtocol", mConfig.serverProtocol);
            mConfig.acceptMinProtocol  = json.value("acceptMinProtocol", mConfig.acceptMinProtocol);
            mConfig.acceptMaxProtocol  = json.value("acceptMaxProtocol", mConfig.acceptMaxProtocol);
            logger.info("[{}] config loaded: {}", kModName, configPath.string());
        } catch (std::exception const& e) {
            logger.error("[{}] config load failed, using defaults: {}", kModName, e.what());
        }
    }

    void printBanner(ll::io::Logger& logger) {
        static char const* banner[] = {
            "     ###  #######  ######   ######  ######   ##      ######",
            "      ##  ##       ##   ## ##      ##   ##  ##      ##   ##",
            "      ##  #####    ######  ##      ######   ##      ##   ##",
            "      ##  ##       ## ##   ##      ##       ##      ##   ##",
            "##    ##  ##       ##  ##  ##      ##       ##      ##   ##",
            " #####   #######  ##   ##  ######  ##       ####### ######",
        };
        logger.info("");
        for (auto line : banner) {
            logger.info("{}", line);
        }
        logger.info("==========================================================================");
        logger.info("  Mod name        : {}", kModName);
        logger.info("  Mod version     : {}", kVersion);
        logger.info(
            "  Server target   : BDS {} (protocol {})",
            mConfig.serverVersion,
            mConfig.serverProtocol
        );
        logger.info(
            "  Client range    : protocol {} - {} (1.26.30 - 1.26.62)",
            mConfig.acceptMinProtocol,
            mConfig.acceptMaxProtocol
        );
        logger.info("  Translation     : {}", mConfig.translationEnabled ? "ON" : "OFF (skeleton)");
        logger.info("  Config file     : config/config.json");
        logger.info("==========================================================================");
        logger.info("");
    }

    ll::mod::NativeMod& mSelf;
    Config              mConfig;
};

} // namespace jercpld

LL_REGISTER_MOD(jercpld::JerCpldMod, jercpld::JerCpldMod::getInstance());
