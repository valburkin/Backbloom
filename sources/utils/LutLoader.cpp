/* LutLoader.cpp
*
*  MIT License
*
*  Copyright (c) 2020-2026 awawa-dev
*
*  Project homesite: https://github.com/awawa-dev/HyperHDR
*
*  Permission is hereby granted, free of charge, to any person obtaining a copy
*  of this software and associated documentation files (the "Software"), to deal
*  in the Software without restriction, including without limitation the rights
*  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
*  copies of the Software, and to permit persons to whom the Software is
*  furnished to do so, subject to the following conditions:
*
*  The above copyright notice and this permission notice shall be included in all
*  copies or substantial portions of the Software.

*  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
*  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
*  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
*  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
*  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
*  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
*  SOFTWARE.
*/
#include <utils/LutLoader.h>
#include <utils/InternalClock.h>
#include <QFile>

#include <utils-zstd/utils-zstd.h>

namespace {
	// the tone mapping code reads 4 bytes per RGB triplet, so the buffer is padded
	const int LUT_MEMORY_ALIGN = 64;
}

bool LutLoader::load(const LoggerName& log, const QList<QString>& files)
{
	_loaded = false;

	for (const QString& fileName3d : files)
	{
		QFile file(fileName3d);
		bool compressed = false;

		if (file.open(QIODevice::ReadOnly)
			|| [&]() {file.setFileName(fileName3d + ".zst"); compressed = true; return file.open(QIODevice::ReadOnly); } ()
			)
		{
			if (log.size())
			{
				Debug(log, "LUT file found: {:s} ({:s})", (file.fileName()), (compressed) ? "compressed" : "uncompressed");
			}

			const qint64 length = file.size();

			if (compressed || length == TABLE_SIZE || length == TABLE_SIZE * 3)
			{
				_lut.resize(TABLE_SIZE + LUT_MEMORY_ALIGN);

				if (compressed)
					_loaded = decompress(log, file);
				else
					_loaded = (file.read(reinterpret_cast<char*>(_lut.data()), TABLE_SIZE) == TABLE_SIZE);

				if (log.size())
				{
					if (_loaded) Info(log, "Found and loaded LUT: '{:s}'", (fileName3d));
					else Error(log, "Error reading LUT file {:s}", (fileName3d));
				}
			}
			else if (log.size())
			{
				Error(log, "LUT file has invalid length: {:d} vs {:d} => {:s}", length, TABLE_SIZE, (fileName3d));
			}

			return _loaded;
		}
		else if (log.size())
		{
			Warning(log, "LUT file is not found here: {:s}", (fileName3d));
		}
	}

	if (log.size()) Error(log, "Could not find any required LUT file");

	return false;
}

bool LutLoader::isLoaded() const
{
	return _loaded;
}

const uint8_t* LutLoader::data() const
{
	return (_loaded) ? _lut.data() : nullptr;
}

bool LutLoader::decompress(const LoggerName& log, QFile& file)
{
	auto now = InternalClock::nowPrecise();
	QByteArray compressedFile = file.readAll();
	const char* retVal = DecompressZSTD(compressedFile.size(), reinterpret_cast<uint8_t*>(compressedFile.data()), _lut.data(), 0, TABLE_SIZE);

	if (retVal != nullptr && log.size())
	{
		Error(log, "Error while decompressing LUT: {:s}", retVal);
	}

	if (log.size()) Info(log, "Decompression took {:f} seconds", (InternalClock::nowPrecise() - now) / 1000.0);

	return retVal == nullptr;
}
