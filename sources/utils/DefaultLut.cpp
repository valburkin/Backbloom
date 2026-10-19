/* DefaultLut.cpp
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
#ifndef PCH_ENABLED
	#include <QFile>
	#include <QDir>
#endif

#include <QThreadPool>

#include <atomic>
#include <cmath>

#include <linalg.h>
#include <utils/DefaultLut.h>
#include <utils/LutLoader.h>
#include <utils/Logger.h>
#include <image/MemoryBuffer.h>
#include <infinite-color-engine/ColorSpace.h>

using namespace linalg;
using namespace aliases;
using namespace ColorSpaceMath;

namespace
{
	// Calibration of the default table (PQ transfer, BT.601 limited range YUV as intermediate format,
	// BT.2020 primaries converted with an adjusted matrix and a per-channel color aspect correction).
	const double4x4 YUV_TO_LINEAR_RGB{
		{ 1.16438356164, 1.16438356164, 1.16438356164, 0 },
		{ -4.43119136738e-17, -0.387076923077, 2.02178571429, 0 },
		{ 1.58691964286, -0.821942994505, -1.77247654695e-16, 0 },
		{ -0.869630789302, 0.53382122535, -1.08791650359, 1 } };
	const double3x3 BT2020_TO_SRGB{
		{ 1.62995144161, -0.159968936426, -0.0191389994477 },
		{ -0.556261837808, 1.17281602107, -0.104271698501 },
		{ -0.0736896038051, -0.0128470846442, 1.12341069795 } };
	const double3 YUV_ASPECT{ 0.9925, 1, 1.035 };
	const double3 COLOR_ASPECT{ 1.06441799482, 1.00700595321, 1.06831049137 };
	constexpr double NITS = 229.625687;

	// RGB to limited range BT.601 YUV
	double4x4 rgbToYuvMatrix()
	{
		const double Kr = 0.2990;
		const double Kb = 0.1140;
		const double Kg = 1.0 - Kr - Kb;
		const double Cr = 0.5 / (1.0 - Kb);
		const double Cb = 0.5 / (1.0 - Kr);
		const double scaleY = 219 / 255.0, addY = 16 / 255.0, scaleUV = 224 / 255.0, addUV = 128 / 255.0;

		double4 c1(Kr * scaleY, -Kr * Cr * scaleUV, (1 - Kr) * Cb * scaleUV, 0);
		double4 c2(Kg * scaleY, -Kg * Cr * scaleUV, -Kg * Cb * scaleUV, 0);
		double4 c3(Kb * scaleY, (1 - Kb) * Cr * scaleUV, -Kb * Cb * scaleUV, 0);
		double4 c4(addY, addUV, addUV, 1);

		return double4x4(c1, c2, c3, c4);
	}

	double3 transform(const double4x4& matrix, const double3& input)
	{
		double4 ret = mul(matrix, double4(input, 1));
		return double3(ret.x, ret.y, ret.z);
	}

	byte3 toneMap(const double4x4& rgbToYuv, const byte3& rgb)
	{
		double3 yuv = transform(rgbToYuv, static_cast<double3>(rgb) / 255.0);
		const byte3 yuvBytes = round_to_0_255<byte3>(yuv * 255.0);
		const bool white = (yuvBytes.y == yuvBytes.z && yuvBytes.y == 128);

		const double low = 16.0 / 255.0;
		yuv.x = (yuv.x < low) ? low : (yuv.x - low) * YUV_ASPECT.x + low;

		if (!white)
		{
			const double mid = 128.0 / 256.0;
			yuv.y = (yuv.y - mid) * YUV_ASPECT.y + mid;
			yuv.z = (yuv.z - mid) * YUV_ASPECT.z + mid;
		}

		const double3 linear = PQ_ST2084(10000.0 / NITS, transform(YUV_TO_LINEAR_RGB, yuv));
		double3 srgb = srgb_linear_to_nonlinear(mul(BT2020_TO_SRGB, linear));

		if (!white)
			srgb *= COLOR_ASPECT;
		else
			srgb *= (COLOR_ASPECT.x + COLOR_ASPECT.y + COLOR_ASPECT.z) / 3.0;

		return round_to_0_255<byte3>(srgb * 255.0);
	}
}

void DefaultLut::fillTable(uint8_t* table)
{
	const double4x4 rgbToYuv = rgbToYuvMatrix();
	QThreadPool threadPool;
	const int step = static_cast<int>(std::ceil(256.0 / threadPool.maxThreadCount()));

	for (int blueBegin = 0; blueBegin < 256; blueBegin += step)
	{
		const int blueEnd = std::min(blueBegin + step, 256);
		threadPool.start([=]() {
			for (int b = blueBegin; b < blueEnd; b++)
				for (int g = 0; g < 256; g++)
					for (int r = 0; r < 256; r++)
					{
						const byte3 result = toneMap(rgbToYuv, byte3(r, g, b));
						uint8_t* entry = &table[LUT_INDEX(r, g, b)];
						entry[0] = result.x;
						entry[1] = result.y;
						entry[2] = result.z;
					}
		});
	}
	threadPool.waitForDone();
}

QString DefaultLut::createFile(const QString& directory)
{
	const QString finalFileName = QDir(directory).filePath("lut_lin_tables.3d");
	QFile file(finalFileName + ".tmp");

	if (!file.open(QIODevice::WriteOnly | QIODevice::Truncate))
		return QString("Could not open LUT file for writing: %1").arg(file.fileName());

	MemoryBuffer<uint8_t> table(LutLoader::TABLE_SIZE);
	fillTable(table.data());

	const bool written = (file.write(reinterpret_cast<const char*>(table.data()), LutLoader::TABLE_SIZE) == LutLoader::TABLE_SIZE);
	file.close();

	if (!written)
	{
		file.remove();
		return QString("Could not write the LUT file: %1").arg(file.fileName());
	}

	QFile::remove(finalFileName);
	if (!file.rename(finalFileName))
		return QString("Could not rename %1 to %2").arg(file.fileName()).arg(finalFileName);

	return QString();
}

void DefaultLut::createFileInBackground(const QString& directory)
{
	static std::atomic<bool> requested = false;

	if (requested.exchange(true))
		return;

	QThreadPool::globalInstance()->start([directory]() {
		LoggerName log("LUT_CREATOR");
		Info(log, "Creating the default LUT in: {:s}", (directory));

		QString errorMessage = createFile(directory);
		if (!errorMessage.isEmpty())
			Error(log, "Error while creating LUT: {:s}", (errorMessage));
		else
			Info(log, "The default LUT has been created: {:s}/lut_lin_tables.3d", (directory));
	});
}
