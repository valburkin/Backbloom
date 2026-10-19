#pragma once

#ifndef PCH_ENABLED
	#include <QString>
#endif

namespace hyperhdr
{
	enum Components
	{
		COMP_INVALID,
		COMP_ALL,
		COMP_HDR,
		COMP_SMOOTHING,
		COMP_BLACKBORDER,
		COMP_VIDEOGRABBER,
		COMP_SYSTEMGRABBER,
		COMP_COLOR,
		COMP_IMAGE,
		COMP_EFFECT,
		COMP_LEDDEVICE,
	};

	inline const char* componentToString(Components c)
	{
		switch (c)
		{
		case COMP_ALL:           return "HyperHDR";
		case COMP_HDR:			 return "HDR (global)";
		case COMP_SMOOTHING:     return "Smoothing";
		case COMP_BLACKBORDER:   return "Blackborder detector";
		case COMP_VIDEOGRABBER:  return "Video capture device";
		case COMP_SYSTEMGRABBER: return "System capture device";
		case COMP_COLOR:         return "Solid color";
		case COMP_EFFECT:        return "Effect";
		case COMP_IMAGE:         return "Image";
		case COMP_LEDDEVICE:     return "LED device";
		default:                 return "";
		}
	}

	inline const char* componentToIdString(Components c)
	{
		switch (c)
		{
		case COMP_ALL:           return "ALL";
		case COMP_HDR:			 return "HDR";
		case COMP_SMOOTHING:     return "SMOOTHING";
		case COMP_BLACKBORDER:   return "BLACKBORDER";
		case COMP_VIDEOGRABBER:  return "VIDEOGRABBER";
		case COMP_SYSTEMGRABBER: return "SYSTEMGRABBER";
		case COMP_COLOR:         return "COLOR";
		case COMP_EFFECT:        return "EFFECT";
		case COMP_IMAGE:         return "IMAGE";
		case COMP_LEDDEVICE:     return "LEDDEVICE";
		default:                 return "";
		}
	}

	inline Components stringToComponent(const QString& component)
	{
		const QString cmp = component.toUpper();
		if (cmp == "ALL")           return COMP_ALL;
		if (cmp == "HDR")			return COMP_HDR;
		if (cmp == "SMOOTHING")     return COMP_SMOOTHING;
		if (cmp == "BLACKBORDER")   return COMP_BLACKBORDER;
		if (cmp == "VIDEOGRABBER")  return COMP_VIDEOGRABBER;
		if (cmp == "SYSTEMGRABBER") return COMP_SYSTEMGRABBER;
		if (cmp == "COLOR")         return COMP_COLOR;
		if (cmp == "EFFECT")        return COMP_EFFECT;
		if (cmp == "IMAGE")         return COMP_IMAGE;
		if (cmp == "LEDDEVICE")     return COMP_LEDDEVICE;
		return COMP_INVALID;
	}

	enum SystemComponent { SUSPEND, LOCKER, MONITOR };
}
