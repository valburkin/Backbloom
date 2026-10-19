#pragma once

#ifndef PCH_ENABLED
	#include <QString>
	#include <QJsonDocument>
#endif

namespace settings {

	enum class type {
		SNDEFFECT = 1,
		BGEFFECT,
		FGEFFECT,
		BLACKBORDER,
		COLOR,
		DEVICE,
		EFFECTS,
		GENERAL,
		SYSTEMGRABBER,
		LEDCONFIG,
		LEDS,
		LOGGER,
		SMOOTHING,
		WEBSERVER,
		SYSTEMCONTROL,
		NETWORK,
		MQTT,
		LAYOUT,
		INVALID
	};

	inline QString typeToString(type type)
	{
		switch (type)
		{
		case type::SNDEFFECT:     return "soundEffect";
		case type::BGEFFECT:      return "backgroundEffect";
		case type::FGEFFECT:      return "foregroundEffect";
		case type::BLACKBORDER:   return "blackborderdetector";
		case type::COLOR:         return "color";
		case type::DEVICE:        return "device";
		case type::EFFECTS:       return "effects";
		case type::GENERAL:       return "general";
		case type::SYSTEMGRABBER: return "systemGrabber";
		case type::LEDCONFIG:     return "ledConfig";
		case type::LEDS:          return "leds";
		case type::LOGGER:        return "logger";
		case type::SMOOTHING:     return "smoothing";
		case type::WEBSERVER:     return "webConfig";
		case type::SYSTEMCONTROL: return "systemControl";
		case type::NETWORK:       return "network";
		case type::MQTT:          return "mqtt";
		case type::LAYOUT:        return "layout";
		default:                  return "invalid";
		}
	}

	inline type stringToType(const QString& type)
	{
		if (type == "soundEffect")               return type::SNDEFFECT;
		else if (type == "backgroundEffect")     return type::BGEFFECT;
		else if (type == "foregroundEffect")     return type::FGEFFECT;
		else if (type == "blackborderdetector")  return type::BLACKBORDER;
		else if (type == "color")                return type::COLOR;
		else if (type == "device")               return type::DEVICE;
		else if (type == "effects")              return type::EFFECTS;
		else if (type == "general")              return type::GENERAL;
		else if (type == "systemGrabber")        return type::SYSTEMGRABBER;
		else if (type == "ledConfig")            return type::LEDCONFIG;
		else if (type == "leds")                 return type::LEDS;
		else if (type == "logger")               return type::LOGGER;
		else if (type == "smoothing")            return type::SMOOTHING;
		else if (type == "webConfig")            return type::WEBSERVER;
		else if (type == "systemControl")        return type::SYSTEMCONTROL;
		else if (type == "network")              return type::NETWORK;
		else if (type == "mqtt")                 return type::MQTT;
		else if (type == "layout")               return type::LAYOUT;
		else                                     return type::INVALID;
	}
}
