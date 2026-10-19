#pragma once

#ifndef PCH_ENABLED
	#include <QString>
	#include <QList>
	#include <QFile>
#endif

#include <image/MemoryBuffer.h>
#include <utils/Logger.h>

// Byte offset of an RGB triplet in a 256x256x256 HDR tone mapping table (red is the fastest changing index)
constexpr uint32_t LUT_INDEX(uint32_t r, uint32_t g, uint32_t b)
{
	return (r + (g << 8) + (b << 16)) * 3;
}

// Loads the HDR to SDR tone mapping table of the system grabber from 'lut_lin_tables.3d' (or its '.zst'
// compressed variant). Files with the legacy layout of three tables (HDR RGB, HDR YUV, YUV) are accepted too:
// the HDR RGB table comes first in both layouts.
class LutLoader
{
public:
	static constexpr int TABLE_SIZE = 256 * 256 * 256 * 3;

	bool load(const LoggerName& log, const QList<QString>& files);
	bool isLoaded() const;
	const uint8_t* data() const;

private:
	bool decompress(const LoggerName& log, QFile& file);

	bool _loaded = false;
	MemoryBuffer<uint8_t> _lut;
};
