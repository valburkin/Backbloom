/* BgraDecoder.cpp
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
	#include <algorithm>
#endif

#include <grabber/windows/DX/BgraDecoder.h>
#include <utils/LutLoader.h>

void decodeBGRA(Image<ColorRgb>& image, int targetSizeX, int targetSizeY,
	int startX, int startY,
	const uint8_t* source, int actualWidth, int actualHeight,
	int division, const uint8_t* lutBuffer, int lineSize)
{
	uint32_t	indexLut;
	uint8_t		buffer[8];
	size_t		divisionX = (size_t)division * 4;

	if (lineSize == 0)
		lineSize = actualWidth * 4;

	for (int j = 0; j < targetSizeY; j++)
	{
		size_t lineSource = std::min(startY + j * division, actualHeight - 1);
		uint8_t* dLine = (image.rawMem() + (size_t)j * targetSizeX * 3);
		uint8_t* dLineEnd = dLine + (size_t)targetSizeX * 3;
		const uint8_t* sLine = ((source + (lineSource * lineSize) + ((size_t)startX * 4)));

		if (lutBuffer == nullptr)
		{
			sLine += 2;
			while (dLine < dLineEnd)
			{
				*dLine++ = *sLine--;
				*dLine++ = *sLine--;
				*dLine++ = *sLine;
				sLine += divisionX + 2;
			}
		}
		else while (dLine < dLineEnd)
		{
			*((uint32_t*)&buffer) = *((const uint32_t*)sLine);
			sLine += divisionX;
			indexLut = LUT_INDEX(buffer[2], buffer[1], buffer[0]);
			*((uint32_t*)dLine) = *((const uint32_t*)(&lutBuffer[indexLut]));
			dLine += 3;
		}
	}
}
